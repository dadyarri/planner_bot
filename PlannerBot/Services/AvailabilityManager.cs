using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Background;
using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;
using User = PlannerBot.Data.User;

namespace PlannerBot.Services;

/// <summary>
/// Manages user availability responses and game planning.
/// Handles updating availability for dates and saving/scheduling games.
/// </summary>
public class AvailabilityManager
{
    private readonly AppDbContext _db;
    private readonly ITelegramBotClient _bot;
    private readonly ITimeTickerManager<TimeTickerEntity> _ticker;
    private readonly TimeZoneUtilities _timeZoneUtilities;
    private readonly ILogger<AvailabilityManager> _logger;

    private static readonly TimeSpan[] ReminderIntervals =
    [
        TimeSpan.FromHours(48), TimeSpan.FromHours(24), TimeSpan.FromHours(5), TimeSpan.FromHours(3),
        TimeSpan.FromHours(1),
        TimeSpan.FromMinutes(10)
    ];

    public AvailabilityManager(
        AppDbContext db,
        ITelegramBotClient bot,
        ITimeTickerManager<TimeTickerEntity> ticker,
        TimeZoneUtilities timeZoneUtilities, ILogger<AvailabilityManager> logger)
    {
        _db = db;
        _bot = bot;
        _ticker = ticker;
        _timeZoneUtilities = timeZoneUtilities;
        _logger = logger;
    }

    /// <summary>
    /// Updates user's availability response for a specific date.
    /// Returns suitable time if all users are available on that date.
    /// </summary>
    public async Task<DateTime?> UpdateResponseForDate(
        Telegram.Bot.Types.User from,
        Availability availability,
        DateTime date,
        string? args = null)
    {
        var time = TimeSpan.Zero;
        if (args is not null)
        {
            var culture = _timeZoneUtilities.GetRussianCultureInfo();
            var dt = DateTime.ParseExact(args, "HH:mm", culture);
            time = dt.TimeOfDay;
        }

        var dateTime = date.Add(time);
        var utcDateTime = _timeZoneUtilities.ConvertToUtc(dateTime);

        var response = await _db.Responses.Where(r =>
                r.User.Username == from.Username &&
                r.DateTime.HasValue && r.DateTime.Value.Date == utcDateTime.Date)
            .FirstOrDefaultAsync();

        if (response is not null)
        {
            response.Availability = availability;
            response.DateTime = utcDateTime;
        }
        else
        {
            var user = await _db.Users.Where(u => u.Username == from.Username)
                .FirstOrDefaultAsync();

            if (user is null)
            {
                user = new User
                {
                    Username = from.Username ?? throw new UnreachableException(),
                    Name = $"{from.FirstName} {from.LastName}".Trim(),
                    IsActive = true
                };
                await _db.Users.AddAsync(user);
            }

            response = new Response
            {
                Availability = availability,
                DateTime = utcDateTime,
                User = user
            };
            await _db.Responses.AddAsync(response);
        }

        await _db.SaveChangesAsync();

        var suitableTime = await CheckIfDateIsAvailable(utcDateTime);
        return suitableTime;
    }

    /// <summary>
    /// Checks if all active users are available on a date and returns their common available time.
    /// Returns null if not all users have confirmed or any user declined.
    /// </summary>
    public async Task<DateTime?> CheckIfDateIsAvailable(DateTime date)
    {
        var activeUsersCount = await _db.Users
            .Where(u => u.IsActive)
            .CountAsync();

        var responses = await _db.Responses
            .Where(r =>
                r.DateTime.HasValue && r.DateTime.Value.Date == date.Date &&
                r.User.IsActive)
            .Select(r => new
            {
                r.Availability,
                r.DateTime
            })
            .ToListAsync();

        if (responses.Count != activeUsersCount || responses.Any(r =>
                r.Availability is Availability.No or Availability.Unknown))
            return null;

        if (responses.Any(r => r.DateTime == null || r.DateTime.Value.TimeOfDay == TimeSpan.Zero))
            return null;

        var commonTime = responses
            .Max(r => _timeZoneUtilities.ConvertToMoscow(r.DateTime!.Value));

        return commonTime;
    }

    /// <summary>
    /// Sets availability to "No" (unavailable) for all unmarked days in the plan range (8 days).
    /// This is called when the user finishes planning their weekly availability.
    /// </summary>
    public async Task SetUnavailableForUnmarkedDays(string? username)
    {
        var now = DateTime.UtcNow;
        const int planRangeDays = 8;

        for (var i = 0; i < planRangeDays; i++)
        {
            var date = now.AddDays(i).Date;

            var existingResponse = await _db.Responses
                .Include(r => r.User)
                .Where(r => r.User.Username == username &&
                            r.DateTime.HasValue && r.DateTime.Value.Date == date)
                .FirstOrDefaultAsync();

            // Only set to unavailable if user hasn't marked this day
            if (existingResponse is null)
            {
                var user = await _db.Users.Where(u => u.Username == username)
                    .FirstOrDefaultAsync();

                if (user is not null)
                {
                    // Create a response with No availability for this date
                    var response = new Response
                    {
                        Availability = Availability.No,
                        DateTime = date,
                        User = user
                    };
                    await _db.Responses.AddAsync(response);
                }
            }
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Saves a game and schedules reminders at specified intervals before the game.
    /// </summary>
    public async Task SavePlannedGame(DateTime dateTime, Message message)
    {
        var now = DateTime.UtcNow;
        var dateTimeUtc = _timeZoneUtilities.ConvertToUtc(dateTime);

        // Check if game is in the past
        if (dateTimeUtc < now)
        {
            await _bot.SendMessage(message.Chat.Id,
                messageThreadId: message.MessageThreadId,
                text: "⚠️ Нельзя назначить битву в прошлое!",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());
            return;
        }

        await _db.SavedGame.Where(sg => sg.DateTime <= now.Date).ExecuteDeleteAsync();

        // Check if game already exists on the same day (timezone-aware)
        var allGames = await _db.SavedGame.ToListAsync();
        var moscowGameDate = _timeZoneUtilities.ConvertToMoscow(dateTimeUtc).Date;
        var existingGameOnDate = allGames.Any(sg =>
            _timeZoneUtilities.ConvertToMoscow(sg.DateTime).Date == moscowGameDate);

        if (!existingGameOnDate)
        {
            var savedGame = new SavedGame
            {
                DateTime = dateTimeUtc
            };
            await _db.AddAsync(savedGame);
            await _db.SaveChangesAsync();

            var timeUntilGame = dateTimeUtc - DateTime.UtcNow;
            var culture = _timeZoneUtilities.GetRussianCultureInfo();

            foreach (var timeSpan in ReminderIntervals.Where(i => i <= timeUntilGame))
            {
                var executionTime = dateTimeUtc.Add(-timeSpan);
                var reminderIntervalMinutes = (int)timeSpan.TotalMinutes;

                var schedulingResult = await _ticker.AddAsync(new TimeTickerEntity
                {
                    Function = "send_reminder",
                    ExecutionTime = executionTime,
                    Request = TickerHelper.CreateTickerRequest(new SendReminderJobContext
                    {
                        ReminderIntervalMinutes = reminderIntervalMinutes,
                        ChatId = message.Chat.Id,
                        ThreadId = message.MessageThreadId,
                        SavedGameId = savedGame.Id
                    }),
                });

                if (schedulingResult.IsSucceeded)
                {
                    _logger.LogInformation("Reminder scheduled to {ExecutionTime}", executionTime);
                }
            }

            var sb = new StringBuilder();

            foreach (var game in _db.SavedGame)
            {
                var gameDateTime = _timeZoneUtilities.ConvertToMoscow(game.DateTime);
                var dateStr = gameDateTime.ToString("dd.MM.yyyy (ddd) HH:mm", culture);
                sb.AppendLine($"- {dateStr}");
            }

            await _bot.SendMessage(message.Chat.Id,
                messageThreadId: message.MessageThreadId,
                text: $"""
                       🏰 Битва записана в летописи! Грядущие битвы:

                       {sb}
                       """,
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());
        }
        else
        {
            await _bot.SendMessage(message.Chat.Id,
                messageThreadId: message.MessageThreadId,
                text: "⚔️ На этот день битва уже назначена!",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());
        }
    }

    /// <summary>
    /// Creates a voting session for a specific game datetime.
    /// Returns the stored voting message containing message and chat IDs for tracking reactions.
    /// </summary>
    public async Task<VoteSession?> CreateVotingSession(DateTime gameDateTime, Message message)
    {
        var voteSession = new VoteSession
        {
            ChatId = message.Chat.Id,
            GameDateTime = gameDateTime,
            ThreadId = message.MessageThreadId ?? 0,
            VoteCount = 0,
            CreatedAt = DateTime.UtcNow
        };

        await _db.VoteSessions.AddAsync(voteSession);
        await _db.SaveChangesAsync();

        return voteSession;
    }

    /// <summary>
    /// Increments vote count for a voting session and checks if threshold is reached.
    /// Returns true if all active players have voted.
    /// </summary>
    public async Task<bool> IncrementVoteAndCheckThreshold(long votingMessageId)
    {
        var votingMessage = await _db.VoteSessions.FirstOrDefaultAsync(vm => vm.Id == votingMessageId);
        if (votingMessage is null)
            return false;

        votingMessage.VoteCount++;

        var activeUsersCount = await _db.Users
            .Where(u => u.IsActive)
            .CountAsync();

        await _db.SaveChangesAsync();

        return votingMessage.VoteCount >= activeUsersCount;
    }

    /// <summary>
    /// Decrements vote count for a voting session when a user removes their reaction.
    /// </summary>
    public async Task DecrementVote(long votingMessageId)
    {
        var votingMessage = await _db.VoteSessions.FirstOrDefaultAsync(vm => vm.Id == votingMessageId);
        if (votingMessage is null)
            return;

        if (votingMessage.VoteCount > 0)
        {
            votingMessage.VoteCount--;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Gets a voting message session by ID.
    /// </summary>
    public async Task<VoteSession?> GetVotingMessage(long votingMessageId)
    {
        return await _db.VoteSessions.FirstOrDefaultAsync(vm => vm.Id == votingMessageId);
    }

    /// <summary>
    /// Deletes a voting message session (after threshold is reached or expires).
    /// </summary>
    public async Task DeleteVotingSession(long votingMessageId)
    {
        var votingMessage = await _db.VoteSessions.FirstOrDefaultAsync(vm => vm.Id == votingMessageId);
        if (votingMessage is not null)
        {
            _db.VoteSessions.Remove(votingMessage);
            await _db.SaveChangesAsync();
        }
    }
}

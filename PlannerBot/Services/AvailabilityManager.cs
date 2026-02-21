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
        TimeZoneUtilities timeZoneUtilities)
    {
        _db = db;
        _bot = bot;
        _ticker = ticker;
        _timeZoneUtilities = timeZoneUtilities;
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
    public async Task SavePlannedGame(DateTime dateTime, Message message, ILogger<UpdateHandler> logger)
    {
        var now = DateTime.UtcNow;
        var dateTimeUtc = _timeZoneUtilities.ConvertToUtc(dateTime);

        await _db.SavedGame.Where(sg => sg.DateTime <= now.Date).ExecuteDeleteAsync();

        if (!await _db.SavedGame.AnyAsync(sg => sg.DateTime <= dateTimeUtc))
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
                    logger.LogInformation("Reminder scheduled to {ExecutionTime}", executionTime);
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
}

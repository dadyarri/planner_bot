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

    private static readonly TimeSpan VoteSessionTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan VoteReminderDelay = TimeSpan.FromHours(12);

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

        var dateOnly = date.Date;
        var gameExistsOnDate = await _db.SavedGame
            .Where(sg => sg.DateTime.Date == dateOnly)
            .AnyAsync();

        if (gameExistsOnDate)
            return null;

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
                var dateStr = _timeZoneUtilities.FormatDateTime(gameDateTime);
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
    /// Schedules expiry and reminder jobs. Returns the stored voting session.
    /// </summary>
    public async Task<VoteSession?> CreateVotingSession(DateTime gameDateTime, Message message,
        string creatorUsername)
    {
        var expiresAt = DateTime.UtcNow.Add(VoteSessionTtl);
        var voteSession = new VoteSession
        {
            ChatId = message.Chat.Id,
            GameDateTime = gameDateTime,
            ThreadId = message.MessageThreadId ?? 0,
            VoteCount = 0,
            AgainstCount = 0,
            Outcome = VoteOutcome.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            CreatorUsername = creatorUsername
        };

        await _db.VoteSessions.AddAsync(voteSession);
        await _db.SaveChangesAsync();

        // Schedule expiry job
        await _ticker.AddAsync(new TimeTickerEntity
        {
            Function = "expire_vote_session",
            ExecutionTime = expiresAt,
            Request = TickerHelper.CreateTickerRequest(new VoteSessionExpiryJobContext
            {
                VoteSessionId = voteSession.Id,
                ChatId = message.Chat.Id,
                ThreadId = message.MessageThreadId,
                MessageId = message.MessageId
            })
        });

        // Schedule non-voter reminder
        var reminderTime = DateTime.UtcNow.Add(VoteReminderDelay);
        if (reminderTime < expiresAt)
        {
            await _ticker.AddAsync(new TimeTickerEntity
            {
                Function = "send_vote_reminder",
                ExecutionTime = reminderTime,
                Request = TickerHelper.CreateTickerRequest(new VoteReminderJobContext
                {
                    VoteSessionId = voteSession.Id,
                    ChatId = message.Chat.Id,
                    ThreadId = message.MessageThreadId
                })
            });
        }

        return voteSession;
    }

    /// <summary>
    /// Records a user's vote (for or against) and checks if a final outcome is reached.
    /// Uses per-user tracking for deduplication. Returns the resolved outcome if any.
    /// </summary>
    public async Task<VoteOutcome> RecordVoteAndCheckOutcome(long votingSessionId, long userId, VoteType voteType)
    {
        // Check for duplicate vote
        var existingVote = await _db.VoteSessionVotes
            .AnyAsync(v => v.VoteSessionId == votingSessionId && v.UserId == userId);

        if (existingVote)
            return VoteOutcome.Pending;

        // Record the vote and persist it
        await _db.VoteSessionVotes.AddAsync(new VoteSessionVote
        {
            VoteSessionId = votingSessionId,
            UserId = userId,
            Type = voteType,
            VotedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Atomically increment the appropriate counter
        if (voteType == VoteType.For)
        {
            await _db.VoteSessions
                .Where(vs => vs.Id == votingSessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(vs => vs.VoteCount, vs => vs.VoteCount + 1));
        }
        else
        {
            await _db.VoteSessions
                .Where(vs => vs.Id == votingSessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(vs => vs.AgainstCount, vs => vs.AgainstCount + 1));
        }

        return await EvaluateVoteOutcome(votingSessionId);
    }

    /// <summary>
    /// Removes a user's vote when they remove their reaction.
    /// </summary>
    public async Task RemoveVote(long votingSessionId, long userId, VoteType voteType)
    {
        var vote = await _db.VoteSessionVotes
            .FirstOrDefaultAsync(v => v.VoteSessionId == votingSessionId && v.UserId == userId && v.Type == voteType);

        if (vote is null)
            return;

        _db.VoteSessionVotes.Remove(vote);
        await _db.SaveChangesAsync();

        // Atomically decrement the appropriate counter
        if (voteType == VoteType.For)
        {
            await _db.VoteSessions
                .Where(vs => vs.Id == votingSessionId && vs.VoteCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(vs => vs.VoteCount, vs => vs.VoteCount - 1));
        }
        else
        {
            await _db.VoteSessions
                .Where(vs => vs.Id == votingSessionId && vs.AgainstCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(vs => vs.AgainstCount, vs => vs.AgainstCount - 1));
        }
    }

    /// <summary>
    /// Calculates the minimum number of against votes needed to declare no consensus.
    /// Currently: majority rule — more than half of active users must vote against.
    /// </summary>
    private static int MinimumAgainstVotesForNoConsensus(int activeUsersCount) =>
        (activeUsersCount + 1) / 2;

    /// <summary>
    /// Evaluates whether the voting session has reached a final outcome.
    /// Threshold: all active users voted FOR. No-consensus: against votes >= half of active users.
    /// </summary>
    private async Task<VoteOutcome> EvaluateVoteOutcome(long votingSessionId)
    {
        var session = await _db.VoteSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(vs => vs.Id == votingSessionId);
        if (session is null)
            return VoteOutcome.Pending;

        var activeUsersCount = await _db.Users
            .Where(u => u.IsActive)
            .CountAsync();

        // All active users voted FOR
        if (session.VoteCount >= activeUsersCount)
            return VoteOutcome.Saved;

        // Against votes >= minimum threshold — no consensus
        if (session.AgainstCount > 0 && session.AgainstCount >= MinimumAgainstVotesForNoConsensus(activeUsersCount))
            return VoteOutcome.NoConsensus;

        return VoteOutcome.Pending;
    }

    /// <summary>
    /// Gets a voting session by ID, including voter information.
    /// </summary>
    public async Task<VoteSession?> GetVotingSession(long votingSessionId)
    {
        return await _db.VoteSessions
            .Include(vs => vs.Votes)
            .ThenInclude(v => v.User)
            .FirstOrDefaultAsync(vm => vm.Id == votingSessionId);
    }

    /// <summary>
    /// Gets voter display info for a voting session, grouped by vote type.
    /// </summary>
    public async Task<(List<string> ForVoters, List<string> AgainstVoters)> GetVoterInfo(long votingSessionId)
    {
        var votes = await _db.VoteSessionVotes
            .Where(v => v.VoteSessionId == votingSessionId)
            .Select(v => new { v.User.Username, v.Type })
            .ToListAsync();

        var forVoters = votes.Where(v => v.Type == VoteType.For).Select(v => v.Username).ToList();
        var againstVoters = votes.Where(v => v.Type == VoteType.Against).Select(v => v.Username).ToList();

        return (forVoters, againstVoters);
    }

    /// <summary>
    /// Builds the voting message text with current vote counts and voter lists.
    /// This is the single source of truth for how voting messages are formatted.
    /// </summary>
    public async Task<string> BuildVotingMessageText(VoteSession session)
    {
        var moscowGameDateTime = _timeZoneUtilities.ConvertToMoscow(session.GameDateTime);
        var activeUsersCount = await _db.Users.Where(u => u.IsActive).CountAsync();
        var (forVoters, againstVoters) = await GetVoterInfo(session.Id);

        var sb = new StringBuilder();
        sb.AppendLine(
            $"⚔️ Совет братства решает! {_timeZoneUtilities.FormatDate(moscowGameDateTime)} — час кампании: <b>{_timeZoneUtilities.FormatTime(moscowGameDateTime)}</b>");
        sb.AppendLine();
        sb.AppendLine($"👍 За: {session.VoteCount}/{activeUsersCount}");

        if (forVoters.Count > 0)
            sb.AppendLine($"  └ {string.Join(", ", forVoters.Select(u => $"@{u}"))}");

        if (session.AgainstCount > 0 || againstVoters.Count > 0)
        {
            sb.AppendLine($"👎 Против: {session.AgainstCount}");
            if (againstVoters.Count > 0)
                sb.AppendLine($"  └ {string.Join(", ", againstVoters.Select(u => $"@{u}"))}");
        }

        sb.AppendLine();
        sb.AppendLine("👍 — Поддержать запись битвы в летописи");
        sb.AppendLine("👎 — Отклонить этот час (вас исключат из напоминаний)");

        return sb.ToString();
    }

    /// <summary>
    /// Closes a voting session with the given outcome.
    /// Updates the session record but does not delete it (for history).
    /// </summary>
    public async Task CloseVotingSession(long votingSessionId, VoteOutcome outcome)
    {
        await _db.VoteSessions
            .Where(vs => vs.Id == votingSessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(vs => vs.Outcome, outcome));
    }

    /// <summary>
    /// Sends a voting message to a chat, creates the voting session, and sets the initial reaction.
    /// This is the single shared method for creating voting messages from any entry point.
    /// </summary>
    public async Task SendVotingMessage(
        long chatId,
        int? threadId,
        DateTime utcGameDateTime,
        string creatorUsername,
        string activeMentions,
        KeyboardGenerator keyboard)
    {
        var moscowGameDateTime = _timeZoneUtilities.ConvertToMoscow(utcGameDateTime);
        var sentMessage = await _bot.SendMessage(chatId,
            messageThreadId: threadId,
            text:
            $"⚔️ Совет братства решает! {_timeZoneUtilities.FormatDate(moscowGameDateTime)} — час кампании: <b>{_timeZoneUtilities.FormatTime(moscowGameDateTime)}</b>\n\n👍 — Поддержать запись битвы в летописи\n👎 — Отклонить этот час (вас исключат из напоминаний)\n\n{activeMentions}",
            parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new InlineKeyboardMarkup(keyboard.GenerateVoteCancelKeyboard(creatorUsername)));

        var votingSession = await CreateVotingSession(utcGameDateTime, sentMessage, creatorUsername);
        if (votingSession is not null)
        {
            votingSession.MessageId = sentMessage.MessageId;
            await _db.SaveChangesAsync();

            await _bot.SetMessageReaction(
                chatId,
                sentMessage.MessageId,
                [new ReactionTypeEmoji { Emoji = "👍" }]);
        }
    }

    /// <summary>
    /// Deletes a voting session and its associated votes entirely.
    /// </summary>
    public async Task DeleteVotingSession(long votingSessionId)
    {
        await _db.VoteSessionVotes
            .Where(v => v.VoteSessionId == votingSessionId)
            .ExecuteDeleteAsync();

        var votingMessage = await _db.VoteSessions.FirstOrDefaultAsync(vm => vm.Id == votingSessionId);
        if (votingMessage is not null)
        {
            _db.VoteSessions.Remove(votingMessage);
            await _db.SaveChangesAsync();
        }
    }
}

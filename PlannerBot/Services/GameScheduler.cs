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

namespace PlannerBot.Services;

/// <summary>
/// Handles saving planned games and scheduling reminders.
/// </summary>
public class GameScheduler
{
    private readonly AppDbContext _db;
    private readonly ITelegramBotClient _bot;
    private readonly ITimeTickerManager<TimeTickerEntity> _ticker;
    private readonly TimeZoneUtilities _timeZoneUtilities;
    private readonly ILogger<GameScheduler> _logger;

    private static readonly TimeSpan[] ReminderIntervals =
    [
        TimeSpan.FromHours(48), TimeSpan.FromHours(24), TimeSpan.FromHours(5), TimeSpan.FromHours(3),
        TimeSpan.FromHours(1),
        TimeSpan.FromMinutes(10)
    ];

    public GameScheduler(
        AppDbContext db,
        ITelegramBotClient bot,
        ITimeTickerManager<TimeTickerEntity> ticker,
        TimeZoneUtilities timeZoneUtilities,
        ILogger<GameScheduler> logger)
    {
        _db = db;
        _bot = bot;
        _ticker = ticker;
        _timeZoneUtilities = timeZoneUtilities;
        _logger = logger;
    }

    /// <summary>
    /// Saves a game and schedules reminders at specified intervals before the game.
    /// Returns the message text to display to the user (success or error).
    /// Does not send any Telegram messages — caller is responsible for display.
    /// Reminders are routed to the campaign's ForumThread (Phase 9).
    /// </summary>
    public async Task<string> SavePlannedGame(DateTime dateTime, Message message, int campaignId)
    {
        var now = DateTime.UtcNow;
        var dateTimeUtc = _timeZoneUtilities.ConvertToUtc(dateTime);

        // Check if game is in the past
        if (dateTimeUtc < now)
            return "⚠️ Нельзя назначить битву в прошлое!";

        await _db.SavedGame.Where(sg => sg.DateTime <= now.Date).ExecuteDeleteAsync();

        // Check if game already exists on the same day (timezone-aware) for this campaign
        var allGames = await _db.SavedGame.Where(sg => sg.CampaignId == campaignId).ToListAsync();
        var moscowGameDate = _timeZoneUtilities.ConvertToMoscow(dateTimeUtc).Date;
        var existingGameOnDate = allGames.Any(sg =>
            _timeZoneUtilities.ConvertToMoscow(sg.DateTime).Date == moscowGameDate);

        if (existingGameOnDate)
            return "⚔️ На этот день битва уже назначена!";

        var savedGame = new SavedGame
        {
            DateTime = dateTimeUtc,
            CampaignId = campaignId
        };
        await _db.AddAsync(savedGame);
        await _db.SaveChangesAsync();

        // Load campaign's forum thread for routing reminders to the campaign thread
        var campaign = await _db.Campaigns
            .Include(c => c.ForumThread)
            .FirstAsync(c => c.Id == campaignId);

        var reminderChatId = campaign.ForumThread.ChatId;
        var reminderThreadId = campaign.ForumThread.ThreadId;

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
                    ChatId = reminderChatId,
                    ThreadId = reminderThreadId,
                    SavedGameId = savedGame.Id
                }),
            });

            if (schedulingResult.IsSucceeded)
                _logger.LogInformation("Reminder scheduled to {ExecutionTime}", executionTime);
        }

        var sb = new StringBuilder();
        var upcomingGames = await _db.SavedGame
            .Where(sg => sg.CampaignId == campaignId && sg.DateTime > now)
            .OrderBy(sg => sg.DateTime)
            .ToListAsync();
        foreach (var game in upcomingGames)
        {
            var gameDateTime = _timeZoneUtilities.ConvertToMoscow(game.DateTime);
            sb.AppendLine($"- {_timeZoneUtilities.FormatDateTime(gameDateTime)}");
        }

        return $"✅ Совет единогласен — битва записана в летописи! ⚔️\n\n🏰 Грядущие битвы:\n{sb}";
    }
}

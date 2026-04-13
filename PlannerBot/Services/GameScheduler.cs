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
}

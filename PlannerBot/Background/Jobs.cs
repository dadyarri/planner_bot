using System.Globalization;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using Telegram.Bot;
using TickerQ.Utilities.Base;

namespace PlannerBot.Background;

public class Jobs(ILogger<Jobs> logger, ITelegramBotClient bot, AppDbContext db)
{
    private static readonly CultureInfo RussianCultureInfo = new("ru-RU");

    [TickerFunction("send_reminder")]
    public async Task SendReminder(TickerFunctionContext<SendReminderJobContext> context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending reminder");

        var savedGame = await db.SavedGame.FirstOrDefaultAsync(
            sg => sg.Id == context.Request.SavedGameId, cancellationToken);

        if (savedGame is null)
        {
            logger.LogWarning("SavedGame with ID {SavedGameId} not found", context.Request.SavedGameId);
            return;
        }

        var availablePlayers = await db.Responses
            .Include(r => r.User)
            .Where(r => r.DateTime.HasValue &&
                        r.DateTime.Value.Date == savedGame.DateTime.Date &&
                        (r.Availability == Availability.Yes || r.Availability == Availability.Probably) &&
                        r.User.IsActive)
            .Select(r => r.User.Username)
            .ToListAsync(cancellationToken);

        var availablePlayerTags = availablePlayers.Select(u => $"@{u}").ToList();
        var interval = TimeSpan.FromMinutes(context.Request.ReminderIntervalMinutes);

        var message = $"""
                       {string.Join(", ", availablePlayerTags)}

                       🚨 🚨 🚨 Герольды трубят — битва начнётся через {interval.Humanize(culture: RussianCultureInfo, toWords: true)}! 🚨 🚨 🚨
                       """;

        await bot.SendMessage(context.Request.ChatId, messageThreadId: context.Request.ThreadId,
            text: message, cancellationToken: cancellationToken);
    }

    [TickerFunction("send_weekly_voting_reminder")]
    public async Task SendWeeklyVotingReminder(TickerFunctionContext<WeeklyVotingReminderJobContext> context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending weekly voting reminder");

        var activePlayers = await db.Users
            .Where(u => u.IsActive)
            .Select(u => u.Username)
            .ToListAsync(cancellationToken);

        if (activePlayers.Count == 0)
        {
            logger.LogWarning("No active players found for weekly reminder");
            return;
        }

        var activePlayerTags = activePlayers.Select(u => $"@{u}").ToList();

        var message = $"""
                       {string.Join(", ", activePlayerTags)}

                       ⚔️ Приветствуем героев братства!

                       Пришла пора узреть грядущие дни - примените заклинание предсказания /plan,
                       чтобы объявить о своём присоединении к битвам!

                       🍀 Пусть боги будут благосклонны к вам! 🍀
                       """;

        await bot.SendMessage(context.Request.ChatId, messageThreadId: context.Request.ThreadId,
            text: message, cancellationToken: cancellationToken);
    }
}
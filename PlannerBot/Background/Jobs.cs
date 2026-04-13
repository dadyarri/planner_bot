using Humanizer;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using PlannerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TickerQ.Utilities.Base;

namespace PlannerBot.Background;

public class Jobs(ILogger<Jobs> logger, ITelegramBotClient bot, AppDbContext db, TimeZoneUtilities timeZoneUtilities)
{

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

        // Exclude users who voted against this time slot and inactive users
        var excludedUsernames = new HashSet<string>();

        var inactiveUsernames = await db.Users
            .Where(u => !u.IsActive)
            .Select(u => u.Username)
            .ToListAsync(cancellationToken);
        excludedUsernames.UnionWith(inactiveUsernames);

        var voteSession = await db.VoteSessions
            .Include(vs => vs.Votes)
            .FirstOrDefaultAsync(vs => vs.GameDateTime == savedGame.DateTime, cancellationToken);

        if (voteSession is not null)
        {
            var againstUserIds = voteSession.Votes
                .Where(v => v.Type == VoteType.Against)
                .Select(v => v.UserId)
                .ToHashSet();

            var againstUsernames = await db.Users
                .Where(u => againstUserIds.Contains(u.Id))
                .Select(u => u.Username)
                .ToListAsync(cancellationToken);

            excludedUsernames.UnionWith(againstUsernames);
        }

        availablePlayers = availablePlayers
            .Where(u => !excludedUsernames.Contains(u))
            .ToList();

        var availablePlayerTags = availablePlayers.Select(u => $"@{u}").ToList();
        var interval = TimeSpan.FromMinutes(context.Request.ReminderIntervalMinutes);

        var message = $"""
                       {string.Join(", ", availablePlayerTags)}

                       🚨 🚨 🚨 Герольды трубят — битва начнётся через {interval.Humanize(culture: timeZoneUtilities.GetRussianCultureInfo(), toWords: true)}! 🚨 🚨 🚨
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

    [TickerFunction("expire_vote_session")]
    public async Task ExpireVoteSession(TickerFunctionContext<VoteSessionExpiryJobContext> context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Expiring vote session {VoteSessionId}", context.Request.VoteSessionId);

        var voteSession = await db.VoteSessions
            .FirstOrDefaultAsync(vs => vs.Id == context.Request.VoteSessionId, cancellationToken);

        if (voteSession is null)
        {
            logger.LogInformation("Vote session {VoteSessionId} already deleted", context.Request.VoteSessionId);
            return;
        }

        // Mark the outcome before deleting
        voteSession.Outcome = VoteOutcome.Expired;
        await db.SaveChangesAsync(cancellationToken);

        // Delete associated votes
        await db.VoteSessionVotes
            .Where(v => v.VoteSessionId == voteSession.Id)
            .ExecuteDeleteAsync(cancellationToken);

        db.VoteSessions.Remove(voteSession);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await bot.EditMessageText(
                context.Request.ChatId,
                context.Request.MessageId,
                "⏳ Песочные часы истекли — голосование завершено без достаточного числа голосов. Битва не записана в летописи.",
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to edit expired voting message");
        }
    }

    [TickerFunction("send_vote_reminder")]
    public async Task SendVoteReminder(TickerFunctionContext<VoteReminderJobContext> context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending vote reminder for session {VoteSessionId}", context.Request.VoteSessionId);

        var voteSession = await db.VoteSessions
            .Include(vs => vs.Votes)
            .ThenInclude(v => v.User)
            .FirstOrDefaultAsync(vs => vs.Id == context.Request.VoteSessionId, cancellationToken);

        if (voteSession is null)
        {
            logger.LogInformation("Vote session {VoteSessionId} already completed", context.Request.VoteSessionId);
            return;
        }

        var votedUserIds = voteSession.Votes.Select(v => v.UserId).ToHashSet();

        var nonVoters = await db.Users
            .Where(u => u.IsActive && !votedUserIds.Contains(u.Id))
            .Select(u => u.Username)
            .ToListAsync(cancellationToken);

        if (nonVoters.Count == 0)
            return;

        var nonVoterTags = nonVoters.Select(u => $"@{u}").ToList();

        var message = $"""
                       {string.Join(", ", nonVoterTags)}

                       📢 Голосование ожидает вашего решения!
                       Поставьте 👍 чтобы одобрить запись битвы, или 👎 чтобы отклонить.
                       """;

        await bot.SendMessage(context.Request.ChatId, messageThreadId: context.Request.ThreadId,
            text: message, cancellationToken: cancellationToken);
    }
}
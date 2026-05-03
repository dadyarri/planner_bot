using Humanizer;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using PlannerBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
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

        var savedGame = await db.SavedGame
            .Include(sg => sg.Campaign)
            .ThenInclude(c => c.ForumThread)
            .Include(sg => sg.Campaign)
            .ThenInclude(c => c.DungeonMaster)
            .Include(sg => sg.Campaign)
            .ThenInclude(c => c.Members)
            .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(sg => sg.Id == context.Request.SavedGameId, cancellationToken);

        if (savedGame is null)
        {
            logger.LogWarning("SavedGame with ID {SavedGameId} not found", context.Request.SavedGameId);
            return;
        }

        var interval = TimeSpan.FromMinutes(context.Request.ReminderIntervalMinutes);

        var campaign = savedGame.Campaign;
        var campaignName = campaign.ForumThread.Name;
        var dmMention = $"@{campaign.DungeonMaster.Username}";
        var memberMentions = campaign.Members
            .Where(m => m.User.IsActive && m.UserId != campaign.DungeonMasterId)
            .Select(m => $"@{m.User.Username}");

        var message = $"""
                       🚨 🚨 🚨 Герольды трубят — битва начнётся через {interval.Humanize(culture: timeZoneUtilities.GetRussianCultureInfo(), toWords: true)}! 🚨 🚨 🚨

                       <b>Кампания:</b> {campaignName}
                       <b>Мастер:</b> {dmMention}
                       <b>Игроки:</b> {string.Join(", ", memberMentions)}
                       """;

        await bot.SendMessage(context.Request.ChatId, messageThreadId: context.Request.ThreadId,
            text: message, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
    }

    [TickerFunction("send_weekly_voting_reminder")]
    public async Task SendWeeklyVotingReminder(TickerFunctionContext<WeeklyVotingReminderJobContext> context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending weekly voting reminder");

        var currentCampaign = await db.CampaignOrderStates
            .AsNoTracking()
            .Where(s => s.ChatId == context.Request.ChatId && s.CurrentCampaignId.HasValue)
            .Select(s => s.CurrentCampaignId!.Value)
            .Join(
                db.Campaigns.AsNoTracking().Include(c => c.ForumThread),
                currentCampaignId => currentCampaignId,
                campaign => campaign.Id,
                (_, campaign) => campaign)
            .FirstOrDefaultAsync(
                c => c.IsActive && c.OrderIndex.HasValue,
                cancellationToken);

        if (currentCampaign is null)
        {
            logger.LogWarning(
                "Weekly voting reminder skipped: no current campaign found for chat {ChatId}",
                context.Request.ChatId);
            return;
        }

        var activePlayers = await db.CampaignMembers
            .Include(cm => cm.User)
            .Where(cm => cm.CampaignId == currentCampaign.Id && cm.User.IsActive)
            .Select(cm => cm.User.Username)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (activePlayers.Count == 0)
        {
            logger.LogWarning(
                "No active campaign members found for weekly reminder in campaign {CampaignId}",
                currentCampaign.Id);
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

        await bot.SendMessage(context.Request.ChatId, messageThreadId: currentCampaign.ForumThread.ThreadId,
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

        // Only remind active campaign members who haven't voted yet
        var campaignMemberIds = await db.CampaignMembers
            .Where(cm => cm.CampaignId == voteSession.CampaignId)
            .Select(cm => cm.UserId)
            .ToListAsync(cancellationToken);

        var nonVoters = await db.Users
            .Where(u => campaignMemberIds.Contains(u.Id) && u.IsActive && !votedUserIds.Contains(u.Id))
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
            text: message,
            replyParameters: new ReplyParameters { MessageId = context.Request.MessageId },
            cancellationToken: cancellationToken);
    }
}
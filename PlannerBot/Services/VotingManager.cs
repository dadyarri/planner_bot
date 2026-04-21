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
/// Manages voting sessions for game scheduling.
/// Handles vote creation, tracking, deduplication, and outcome evaluation.
/// </summary>
public class VotingManager(
    AppDbContext db,
    ITelegramBotClient bot,
    ITimeTickerManager<TimeTickerEntity> ticker,
    TimeZoneUtilities timeZoneUtilities)
{
    private static readonly TimeSpan VoteSessionTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan VoteReminderDelay = TimeSpan.FromHours(12);

    /// <summary>
    /// Creates a voting session for a specific game datetime.
    /// Schedules expiry and reminder jobs. Returns the stored voting session.
    /// </summary>
    private async Task<VoteSession?> CreateVotingSession(DateTime gameDateTime, Message message,
        long creatorId, int campaignId)
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
            CreatorId = creatorId,
            CampaignId = campaignId
        };

        await db.VoteSessions.AddAsync(voteSession);
        await db.SaveChangesAsync();

        // Schedule expiry job
        await ticker.AddAsync(new TimeTickerEntity
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
            await ticker.AddAsync(new TimeTickerEntity
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
        var existingVote = await db.VoteSessionVotes
            .AnyAsync(v => v.VoteSessionId == votingSessionId && v.UserId == userId);

        if (existingVote)
            return VoteOutcome.Pending;

        // Record the vote and persist it
        await db.VoteSessionVotes.AddAsync(new VoteSessionVote
        {
            VoteSessionId = votingSessionId,
            UserId = userId,
            Type = voteType,
            VotedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return await EvaluateVoteOutcome(votingSessionId);
    }

    /// <summary>
    /// Removes a user's vote when they remove their reaction.
    /// </summary>
    public async Task RemoveVote(long votingSessionId, long userId, VoteType voteType)
    {
        var vote = await db.VoteSessionVotes
            .FirstOrDefaultAsync(v => v.VoteSessionId == votingSessionId && v.UserId == userId && v.Type == voteType);

        if (vote is null)
            return;

        db.VoteSessionVotes.Remove(vote);
        await db.SaveChangesAsync();

        // Atomically decrement the appropriate counter
        if (voteType == VoteType.For)
        {
            await db.VoteSessions
                .Where(vs => vs.Id == votingSessionId && vs.VoteCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(vs => vs.VoteCount, vs => vs.VoteCount - 1));
        }
        else
        {
            await db.VoteSessions
                .Where(vs => vs.Id == votingSessionId && vs.AgainstCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(vs => vs.AgainstCount, vs => vs.AgainstCount - 1));
        }
    }

    /// <summary>
    /// Calculates the minimum number of against votes needed to declare no consensus.
    /// Currently: at least half of active users must vote against.
    /// </summary>
    private static int MinimumAgainstVotesForNoConsensus(int activeUsersCount) =>
        (activeUsersCount + 1) / 2;

    /// <summary>
    /// Evaluates whether the voting session has reached a final outcome.
    /// Threshold: all active campaign members voted FOR. No-consensus: against votes >= half of campaign members.
    /// </summary>
    private async Task<VoteOutcome> EvaluateVoteOutcome(long votingSessionId)
    {
        var session = await db.VoteSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(vs => vs.Id == votingSessionId);
        if (session is null)
            return VoteOutcome.Pending;

        var activeUsersCount = await db.CampaignMembers
            .Where(cm => cm.CampaignId == session.CampaignId && cm.User.IsActive)
            .CountAsync();

        if (activeUsersCount <= 0)
            return VoteOutcome.Pending;

        // All active campaign members voted FOR
        if (session.VoteCount >= activeUsersCount)
            return VoteOutcome.Saved;

        // Against votes >= minimum threshold — no consensus
        if (session.AgainstCount >= MinimumAgainstVotesForNoConsensus(activeUsersCount))
            return VoteOutcome.NoConsensus;

        return VoteOutcome.Pending;
    }

    /// <summary>
    /// Gets a voting session by ID, including voter information.
    /// </summary>
    public async Task<VoteSession?> GetVotingSession(long votingSessionId)
    {
        return await db.VoteSessions
            .Include(vs => vs.Votes)
            .ThenInclude(v => v.User)
            .FirstOrDefaultAsync(vm => vm.Id == votingSessionId);
    }

    /// <summary>
    /// Gets voter display info for a voting session, grouped by vote type.
    /// </summary>
    private async
        Task<(List<(string Name, string Username)> ForVoters, List<(string Name, string Username)> AgainstVoters)>
        GetVoterInfo(long votingSessionId)
    {
        var votes = await db.VoteSessionVotes
            .Where(v => v.VoteSessionId == votingSessionId)
            .Select(v => new { v.User.Name, v.User.Username, v.Type })
            .ToListAsync();

        var forVoters = votes.Where(v => v.Type == VoteType.For).Select(v => (v.Name, v.Username)).ToList();
        var againstVoters = votes.Where(v => v.Type == VoteType.Against).Select(v => (v.Name, v.Username)).ToList();

        return (forVoters, againstVoters);
    }

    /// <summary>
    /// Builds the voting message text with current vote counts and voter lists.
    /// This is the single source of truth for how voting messages are formatted.
    /// </summary>
    public async Task<string> BuildVotingMessageText(VoteSession session)
    {
        var moscowGameDateTime = timeZoneUtilities.ConvertToMoscow(session.GameDateTime);
        var activeUsers = await db.CampaignMembers
            .Include(cm => cm.User)
            .Where(cm => cm.CampaignId == session.CampaignId && cm.User.IsActive)
            .ToListAsync();
        var (forVoters, againstVoters) = await GetVoterInfo(session.Id);

        var sb = new StringBuilder();
        sb.AppendLine(
            $"⚔️ Совет братства решает! {timeZoneUtilities.FormatDate(moscowGameDateTime)} — час кампании: <b>{timeZoneUtilities.FormatTime(moscowGameDateTime)}</b>");
        sb.AppendLine();
        sb.AppendLine($"👍 За: {forVoters.Count}/{activeUsers.Count}");

        if (forVoters.Count > 0)
            sb.AppendLine($"  └ {string.Join(", ", forVoters)}");

        if (session.AgainstCount > 0 || againstVoters.Count > 0)
        {
            sb.AppendLine($"👎 Против: {againstVoters.Count}");
            if (againstVoters.Count > 0)
                sb.AppendLine($"  └ {string.Join(", ", againstVoters)}");
        }

        sb.AppendLine();
        sb.AppendLine("👍 — Поддержать запись битвы в летописи");
        sb.AppendLine("👎 — Отклонить этот час");

        var excluded = new HashSet<string>(forVoters.Select(v => v.Username));
        excluded.UnionWith(againstVoters.Select(v => v.Username));

        var leftUsers = activeUsers
            .Where(u => !excluded.Contains(u.User.Username))
            .ToList();

        if (leftUsers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"Совет ждёт ваших голосов: {string.Join(", ", leftUsers.Select(u => $"@{u.User.Username}"))}!");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Closes a voting session with the given outcome.
    /// Updates the session record but does not delete it (for history).
    /// </summary>
    public async Task CloseVotingSession(long votingSessionId, VoteOutcome outcome)
    {
        await db.VoteSessions
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
        long creatorUserId,
        string activeMentions,
        KeyboardGenerator keyboard,
        int campaignId)
    {
        var creator = await db.Users.FirstAsync(u => u.Id == creatorUserId);
        var moscowGameDateTime = timeZoneUtilities.ConvertToMoscow(utcGameDateTime);
        var sentMessage = await bot.SendMessage(chatId,
            messageThreadId: threadId,
            text:
            $"⚔️ Совет братства решает! {timeZoneUtilities.FormatDate(moscowGameDateTime)} — час кампании: <b>{timeZoneUtilities.FormatTime(moscowGameDateTime)}</b>\n\n👍 — Поддержать запись битвы в летописи\n👎 — Отклонить этот час\n\n{activeMentions}",
            parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new InlineKeyboardMarkup(keyboard.GenerateVoteCancelKeyboard(creatorUserId)));

        var votingSession = await CreateVotingSession(utcGameDateTime, sentMessage, creator.Id, campaignId);
        if (votingSession is not null)
        {
            votingSession.MessageId = sentMessage.MessageId;
            await db.SaveChangesAsync();

            await bot.SetMessageReaction(
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
        await db.VoteSessionVotes
            .Where(v => v.VoteSessionId == votingSessionId)
            .ExecuteDeleteAsync();

        await db.VoteSessions
            .Where(vs => vs.Id == votingSessionId)
            .ExecuteDeleteAsync();
    }
}
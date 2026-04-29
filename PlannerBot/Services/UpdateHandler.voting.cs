using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using User = PlannerBot.Data.User;

namespace PlannerBot.Services;

public partial class UpdateHandler
{
    private async Task HandleVoteCancelCallback(CallbackQuery callbackQuery, string[] split)
    {
        var creatorUserId = long.Parse(split[1]);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, creatorUserId);
        if (user is null)
            return;

        var voteSession = await db.VoteSessions
            .FirstOrDefaultAsync(vs =>
                vs.ChatId == callbackQuery.Message!.Chat.Id &&
                vs.MessageId == callbackQuery.Message.Id);

        if (voteSession is not null)
        {
            await votingManager.CloseVotingSession(voteSession.Id, VoteOutcome.Canceled);
            await votingManager.DeleteVotingSession(voteSession.Id);
        }

        await bot.EditMessageText(
            callbackQuery.Message!.Chat.Id,
            callbackQuery.Message.Id,
            "🛑 Голосование отменено создателем — совет распущен",
            parseMode: ParseMode.Html);
    }

    private async Task HandleVotePickCampaignCallback(CallbackQuery callbackQuery, string[] split)
    {
        var campaignId = int.Parse(split[1]);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
        if (user is null)
            return;

        var campaign = await db.Campaigns
            .Include(c => c.ForumThread)
            .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);

        if (campaign is null)
        {
            await bot.EditMessageText(
                callbackQuery.Message!.Chat.Id,
                callbackQuery.Message.Id,
                "⚠️ Кампания не найдена или более не активна.",
                parseMode: ParseMode.Html);
            return;
        }

        if (!authorizationService.CanManageCampaign(campaign, user))
        {
            await bot.EditMessageText(
                callbackQuery.Message!.Chat.Id,
                callbackQuery.Message.Id,
                "⚠️ Только Мастер Подземелий может начать голосование!",
                parseMode: ParseMode.Html);
            return;
        }

        await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
        await commandHandler.ShowSlotPickerForCampaign(
            callbackQuery.Message.Chat.Id,
            callbackQuery.Message.MessageThreadId,
            campaignId,
            user.Id);
    }

    private async Task HandleVotePickSlotCallback(CallbackQuery callbackQuery, string[] split)
    {
        var campaignId = int.Parse(split[1]);
        var slotUtc = DateTime.SpecifyKind(
            DateTime.ParseExact(split[2], "yyMMddHHmm", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[3]));
        if (user is null)
            return;

        var campaign = await GetActiveCampaignForVoting(campaignId);
        if (campaign is null)
        {
            await bot.EditMessageText(
                callbackQuery.Message!.Chat.Id,
                callbackQuery.Message.Id,
                "⚠️ Кампания не найдена или более не активна.",
                parseMode: ParseMode.Html);
            return;
        }

        if (!authorizationService.CanManageCampaign(campaign, user))
        {
            await bot.EditMessageText(
                callbackQuery.Message!.Chat.Id,
                callbackQuery.Message.Id,
                "⚠️ Только Мастер Подземелий может начать голосование!",
                parseMode: ParseMode.Html);
            return;
        }

        await StartVoteFromCampaignSelection(
            callbackQuery.Message!,
            campaign,
            slotUtc,
            user.Id,
            deleteSourceMessage: true);
    }

    private async Task HandleVoteConfirmCallback(CallbackQuery callbackQuery, string[] split)
    {
        var campaignId = int.Parse(split[1]);
        var slotUtc = DateTime.SpecifyKind(
            DateTime.ParseExact(split[2], "yyMMddHHmm", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[3]));
        if (user is null)
            return;

        var campaign = await GetActiveCampaignForVoting(campaignId);
        if (campaign is null)
        {
            await bot.EditMessageText(
                callbackQuery.Message!.Chat.Id,
                callbackQuery.Message.Id,
                "⚠️ Кампания не найдена или более не активна.",
                parseMode: ParseMode.Html);
            return;
        }

        if (!authorizationService.CanManageCampaign(campaign, user))
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "⚠️ Только Мастер может начать голосование!");
            return;
        }

        await StartVoteFromCampaignSelection(
            callbackQuery.Message!,
            campaign,
            slotUtc,
            user.Id,
            deleteSourceMessage: true);
    }

    private async Task HandleVoteCampaignPickCallback(CallbackQuery callbackQuery, string[] split)
    {
        var campaignId = int.Parse(split[1]);
        var slotUtc = DateTime.SpecifyKind(
            DateTime.ParseExact(split[2], "yyMMddHHmm", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[3]));
        if (user is null)
            return;

        var campaign = await GetActiveCampaignForVoting(campaignId);
        if (campaign is null)
        {
            await bot.EditMessageText(
                callbackQuery.Message!.Chat.Id,
                callbackQuery.Message.Id,
                "⚠️ Кампания не найдена или более не активна.",
                parseMode: ParseMode.Html);
            return;
        }

        if (!authorizationService.CanManageCampaign(campaign, user))
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "⚠️ Только Мастер Подземелий может начать голосование!");
            return;
        }

        await StartVoteFromCampaignSelection(
            callbackQuery.Message!,
            campaign,
            slotUtc,
            user.Id,
            deleteSourceMessage: true);
    }

    private async Task<Campaign?> GetActiveCampaignForVoting(int campaignId)
    {
        return await db.Campaigns
            .Include(c => c.ForumThread)
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);
    }

    private async Task StartVoteFromCampaignSelection(
        Message sourceMessage,
        Campaign campaign,
        DateTime slotUtc,
        long userId,
        bool deleteSourceMessage)
    {
        var memberUsers = await GetActiveCampaignMemberUsers(campaign);
        var conflictingCampaigns = await commandHandler.GetConflictingCampaignNames(
            campaign.Id, slotUtc, memberUsers.Select(u => u.Id).ToList());

        if (conflictingCampaigns.Count > 0)
        {
            var conflictList = string.Join("\n", conflictingCampaigns.Select(n => $"  — {n}"));
            var collisionKeyboard =
                keyboardGenerator.GenerateVoteCollisionKeyboard(campaign.Id, slotUtc, userId);

            if (deleteSourceMessage)
            {
                await bot.EditMessageText(
                    sourceMessage.Chat.Id,
                    sourceMessage.Id,
                    $"⚠️ Внимание, Мастер! В этот день уже записаны битвы в других кампаниях:\n\n{conflictList}\n\nНекоторые воины могут быть заняты. Продолжить голосование?",
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(collisionKeyboard));
            }
            else
            {
                await bot.SendMessage(
                    sourceMessage.Chat.Id,
                    messageThreadId: sourceMessage.MessageThreadId,
                    text:
                    $"⚠️ Внимание, Мастер! В этот день уже записаны битвы в других кампаниях:\n\n{conflictList}\n\nНекоторые воины могут быть заняты. Продолжить голосование?",
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(collisionKeyboard));
            }

            return;
        }

        var activeMentions = string.Join(" ", memberUsers
            .Where(u => !string.IsNullOrWhiteSpace(u.Username))
            .Select(u => $"@{u.Username}"));

        if (deleteSourceMessage)
            await bot.DeleteMessage(sourceMessage.Chat.Id, sourceMessage.Id);

        await votingManager.SendVotingMessage(
            campaign.ForumThread.ChatId,
            campaign.ForumThread.ThreadId,
            slotUtc,
            userId,
            activeMentions,
            keyboardGenerator,
            campaign.Id);
    }

    private async Task<List<User>> GetActiveCampaignMemberUsers(Campaign campaign)
    {
        var memberUserIds = campaign.Members.Select(m => m.UserId).ToList();
        return await db.Users
            .Where(u => memberUserIds.Contains(u.Id) && u.IsActive)
            .ToListAsync();
    }

    private async Task OnMessageReaction(MessageReactionUpdated reactionUpdated)
    {
        var me = await bot.GetMe();
        if (reactionUpdated.User?.Id == me.Id)
            return;

        var hasThumbsUpInNew = reactionUpdated.NewReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👍" });
        var hasThumbsUpInOld = reactionUpdated.OldReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👍" });
        var hasThumbsDownInNew = reactionUpdated.NewReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👎" });
        var hasThumbsDownInOld = reactionUpdated.OldReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👎" });

        var thumbsUpChanged = hasThumbsUpInNew != hasThumbsUpInOld;
        var thumbsDownChanged = hasThumbsDownInNew != hasThumbsDownInOld;
        if (!thumbsUpChanged && !thumbsDownChanged)
            return;

        var votingSession = await db.VoteSessions
            .FirstOrDefaultAsync(vm =>
                vm.ChatId == reactionUpdated.Chat.Id &&
                vm.MessageId == reactionUpdated.MessageId &&
                vm.Outcome == VoteOutcome.Pending);

        if (votingSession is null)
            return;

        var user = await db.Users.FirstOrDefaultAsync(u =>
            reactionUpdated.User != null && u.Username == reactionUpdated.User.Username);
        if (user is null || !user.IsActive)
            return;

        if (!await votingManager.CanUserVote(votingSession.Id, user.Id))
            return;

        var outcome = VoteOutcome.Pending;

        if (thumbsUpChanged && !hasThumbsUpInNew && hasThumbsUpInOld)
            await votingManager.RemoveVote(votingSession.Id, user.Id, VoteType.For);

        if (thumbsDownChanged && !hasThumbsDownInNew && hasThumbsDownInOld)
            await votingManager.RemoveVote(votingSession.Id, user.Id, VoteType.Against);

        if (thumbsUpChanged && hasThumbsUpInNew && !hasThumbsUpInOld)
            outcome = await votingManager.RecordVoteAndCheckOutcome(
                votingSession.Id, user.Id, VoteType.For);

        if (thumbsDownChanged && hasThumbsDownInNew && !hasThumbsDownInOld)
            outcome = await votingManager.RecordVoteAndCheckOutcome(
                votingSession.Id, user.Id, VoteType.Against);

        await HandleVoteOutcome(votingSession, outcome);
    }

    private async Task HandleVoteOutcome(VoteSession votingSession, VoteOutcome outcome)
    {
        switch (outcome)
        {
            case VoteOutcome.Saved:
                await CompleteSavedVoteOutcome(votingSession);
                break;
            case VoteOutcome.NoConsensus:
                await CompleteNoConsensusVoteOutcome(votingSession);
                break;
            default:
                await RefreshPendingVoteMessage(votingSession);
                break;
        }
    }

    private async Task CompleteSavedVoteOutcome(VoteSession votingSession)
    {
        await votingManager.CloseVotingSession(votingSession.Id, VoteOutcome.Saved);

        var messageInfo = new Message
        {
            Chat = new Chat { Id = votingSession.ChatId },
            Id = votingSession.MessageId,
            MessageThreadId = votingSession.ThreadId
        };

        var savedText = await gameScheduler.SavePlannedGame(
            votingSession.GameDateTime, messageInfo, votingSession.CampaignId);
        await votingManager.DeleteVotingSession(votingSession.Id);

        await bot.EditMessageText(
            votingSession.ChatId,
            votingSession.MessageId,
            savedText,
            parseMode: ParseMode.Html);
    }

    private async Task CompleteNoConsensusVoteOutcome(VoteSession votingSession)
    {
        await votingManager.CloseVotingSession(votingSession.Id, VoteOutcome.NoConsensus);
        await votingManager.DeleteVotingSession(votingSession.Id);

        await bot.EditMessageText(
            votingSession.ChatId,
            votingSession.MessageId,
            "⚡ Совет не достиг согласия — слишком много голосов против. Битва не состоится в этот час.",
            parseMode: ParseMode.Html);
    }

    private async Task RefreshPendingVoteMessage(VoteSession votingSession)
    {
        var updatedSession = await votingManager.GetVotingSession(votingSession.Id);
        if (updatedSession is null)
            return;

        var messageText = await votingManager.BuildVotingMessageText(updatedSession);

        try
        {
            await bot.EditMessageText(
                votingSession.ChatId,
                votingSession.MessageId,
                messageText,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyMarkup: new InlineKeyboardMarkup(
                    keyboardGenerator.GenerateVoteCancelKeyboard(updatedSession.CreatorId)));
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to update voting message");
        }
    }
}

using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using User = PlannerBot.Data.User;

namespace PlannerBot.Services;

public partial class UpdateHandler
{
    private async Task HandleCampaignJoinPickCallback(CallbackQuery callbackQuery, string[] split)
    {
        var campaignId = int.Parse(split[1]);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
        if (user is null)
            return;

        if (!await EnsureSuperAdminCampaignJoinAccess(callbackQuery, user))
            return;

        await campaignJoinDraftService.InitializeDraft(
            user.Id,
            callbackQuery.Message!.Chat.Id,
            campaignId);
        LogSuperAdminPickedCampaignJoinTarget(
            logger,
            user.Id,
            campaignId,
            callbackQuery.Message.Chat.Id);

        await commandHandler.RenderCampaignJoinAdminPicker(
            callbackQuery.Message.Chat.Id,
            callbackQuery.Message.MessageThreadId,
            campaignId,
            user.Id,
            callbackQuery.Message.Id);
    }

    private async Task HandleCampaignJoinToggleCallback(CallbackQuery callbackQuery, string[] split)
    {
        var targetUserId = long.Parse(split[1]);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
        if (user is null)
            return;

        var page = split.Length > 3 ? int.Parse(split[3]) : 0;
        if (!await EnsureSuperAdminCampaignJoinAccess(callbackQuery, user))
            return;

        var chatId = callbackQuery.Message!.Chat.Id;
        var draft = await GetCampaignJoinDraftOrNotify(callbackQuery, user.Id, chatId);
        if (draft is null)
            return;

        var campaign = await campaignManager.GetActiveCampaign(draft.CampaignId);
        if (campaign is null)
        {
            LogCampaignJoinDraftTargetMissing(logger, user.Id, draft.CampaignId, chatId);
            await bot.EditMessageText(chatId, callbackQuery.Message.Id,
                "⚠️ Кампания не найдена или более не активна.",
                parseMode: ParseMode.Html);
            return;
        }

        if (campaign.Members.Any(m => m.UserId == targetUserId))
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "✅ Этот герой уже состоит в кампании.");
            return;
        }

        LogSuperAdminToggledCampaignJoinUser(
            logger,
            user.Id,
            targetUserId,
            campaign.Id,
            chatId);
        await campaignJoinDraftService.ToggleSelectedUser(user.Id, chatId, targetUserId);
        await commandHandler.RenderCampaignJoinAdminPicker(
            chatId,
            callbackQuery.Message.MessageThreadId,
            campaign.Id,
            user.Id,
            callbackQuery.Message.Id,
            page);
    }

    private async Task HandleCampaignJoinPageCallback(CallbackQuery callbackQuery, string[] split)
    {
        var page = int.Parse(split[1]);
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
        if (user is null)
            return;

        if (!await EnsureSuperAdminCampaignJoinAccess(callbackQuery, user))
            return;

        var chatId = callbackQuery.Message!.Chat.Id;
        var draft = await GetCampaignJoinDraftOrNotify(callbackQuery, user.Id, chatId);
        if (draft is null)
            return;

        await commandHandler.RenderCampaignJoinAdminPicker(
            chatId,
            callbackQuery.Message.MessageThreadId,
            draft.CampaignId,
            user.Id,
            callbackQuery.Message.Id,
            page);
    }

    private async Task HandleCampaignJoinSaveCallback(CallbackQuery callbackQuery, string[] split)
    {
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[1]));
        if (user is null)
            return;

        if (!await EnsureSuperAdminCampaignJoinAccess(callbackQuery, user))
            return;

        var chatId = callbackQuery.Message!.Chat.Id;
        var draft = await GetCampaignJoinDraftOrNotify(callbackQuery, user.Id, chatId);
        if (draft is null)
            return;

        var campaign = await campaignManager.GetActiveCampaign(draft.CampaignId);
        if (campaign is null)
        {
            await campaignJoinDraftService.DeleteDraft(user.Id, chatId);
            LogCampaignJoinDraftTargetMissing(logger, user.Id, draft.CampaignId, chatId);
            await bot.EditMessageText(chatId, callbackQuery.Message.Id,
                "⚠️ Кампания не найдена или более не активна.",
                parseMode: ParseMode.Html);
            return;
        }

        var selectedUserIds = (await campaignJoinDraftService.GetSelectedUserIds(user.Id, chatId))
            .Except(campaign.Members.Select(m => m.UserId))
            .ToList();

        if (selectedUserIds.Count == 0)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "⚠️ Сначала отметь героев для призыва.");
            return;
        }

        var (addedUsers, reactivatedUsers, batchError) = await campaignManager.JoinCampaignBatch(
            campaign.Id,
            selectedUserIds,
            reactivateInactiveUsers: true);
        if (batchError is not null)
        {
            await bot.EditMessageText(chatId, callbackQuery.Message.Id,
                batchError,
                parseMode: ParseMode.Html);
            return;
        }

        await campaignJoinDraftService.DeleteDraft(user.Id, chatId);
        LogSuperAdminSavedCampaignJoinDraft(
            logger,
            user.Id,
            campaign.Id,
            chatId,
            addedUsers.Count,
            reactivatedUsers.Count);

        var names = addedUsers.Count == 0
            ? "Никто не был призван."
            : string.Join(", ", addedUsers.Select(u => u.Name));
        var reactivatedText = reactivatedUsers.Count == 0
            ? string.Empty
            : $"\n\n🕯️ Из отшельничества возвращены: {string.Join(", ", reactivatedUsers.Select(u => u.Name))}";
        await bot.EditMessageText(chatId, callbackQuery.Message.Id,
            $"✅ В кампанию <b>{campaign.ForumThread.Name}</b> призваны: {names}{reactivatedText}",
            parseMode: ParseMode.Html);
    }

    private async Task HandleCampaignJoinCancelCallback(CallbackQuery callbackQuery, string[] split)
    {
        var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[1]));
        if (user is null)
            return;

        await campaignJoinDraftService.DeleteDraft(user.Id, callbackQuery.Message!.Chat.Id);
        await bot.DeleteMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id);
    }

    private async Task<bool> EnsureSuperAdminCampaignJoinAccess(CallbackQuery callbackQuery, User user)
    {
        if (authorizationService.IsSuperAdmin(user))
            return true;

        await bot.AnswerCallbackQuery(callbackQuery.Id,
            "🚨 Лишь старший Архимаг владеет этим призывом!");
        return false;
    }

    private async Task<CampaignJoinDraft?> GetCampaignJoinDraftOrNotify(
        CallbackQuery callbackQuery,
        long userId,
        long chatId)
    {
        var draft = await campaignJoinDraftService.GetDraft(userId, chatId);
        if (draft is not null)
            return draft;

        LogMissingCampaignJoinDraft(logger, userId, chatId);
        await bot.AnswerCallbackQuery(callbackQuery.Id,
            "⚠️ Чернила на свитке высохли. Начни обряд заново.");
        return null;
    }
}

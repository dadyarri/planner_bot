using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PlannerBot.Services;

/// <summary>
/// Main update handler for Telegram bot.
/// Routes updates to appropriate handlers and manages callback queries.
/// </summary>
public partial class UpdateHandler(
    ITelegramBotClient bot,
    ILogger<UpdateHandler> logger,
    TimeZoneUtilities timeZoneUtilities,
    KeyboardGenerator keyboardGenerator,
    AvailabilityManager availabilityManager,
    VotingManager votingManager,
    GameScheduler gameScheduler,
    CommandHandler commandHandler,
    SlotCalculator slotCalculator,
    ForumThreadTracker forumThreadTracker,
    CampaignManager campaignManager,
    AppDbContext db) : IUpdateHandler
{
    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await (update switch
        {
            { Message: { } message } => OnMessage(message),
            { CallbackQuery: { } callbackQuery } => OnCallbackQuery(callbackQuery),
            { MessageReaction: { } reactionUpdated } => OnMessageReaction(reactionUpdated),
            _ => UnknownUpdateHandlerAsync(update)
        });
    }

    public async Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source,
        CancellationToken cancellationToken)
    {
        LogHandleerrorException(logger, exception);
        // Cooldown in case of network connection error
        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    private async Task OnMessage(Message msg)
    {
        // Handle forum topic service messages
        if (msg.ForumTopicCreated is not null && msg.MessageThreadId is not null)
        {
            LogForumTopicCreated(logger, msg.Chat.Id, msg.MessageThreadId.Value);
            await forumThreadTracker.OnForumTopicCreated(msg.Chat.Id, msg.MessageThreadId.Value,
                msg.ForumTopicCreated.Name);
            return;
        }

        if (msg.ForumTopicEdited is not null && msg.MessageThreadId is not null)
        {
            LogForumTopicEdited(logger, msg.Chat.Id, msg.MessageThreadId.Value);
            await forumThreadTracker.OnForumTopicEdited(msg.Chat.Id, msg.MessageThreadId.Value,
                msg.ForumTopicEdited.Name);
            return;
        }

        if (msg.ForumTopicClosed is not null && msg.MessageThreadId is not null)
        {
            LogForumTopicClosed(logger, msg.Chat.Id, msg.MessageThreadId.Value);
            await forumThreadTracker.OnForumTopicClosed(msg.Chat.Id, msg.MessageThreadId.Value);
            return;
        }

        if (msg.ForumTopicReopened is not null && msg.MessageThreadId is not null)
        {
            LogForumTopicReopened(logger, msg.Chat.Id, msg.MessageThreadId.Value);
            await forumThreadTracker.OnForumTopicReopened(msg.Chat.Id, msg.MessageThreadId.Value);
            return;
        }

        if (msg.Text is not { } text)
        {
            LogReceivedAMessageOfTypeMessagetype(logger, msg.Type);
        }
        else if (text.StartsWith('/'))
        {
            var space = text.IndexOf(' ');
            if (space < 0) space = text.Length;
            var command = text[..space].ToLower();
            if (command.LastIndexOf('@') is > 0 and var at) // it's a targeted command
            {
                var me = await bot.GetMe();
                if (command[(at + 1)..].Equals(me.Username, StringComparison.OrdinalIgnoreCase))
                    command = command[..at];
                else
                    return; // command was not targeted at me
            }

            await OnCommand(command, text[space..].TrimStart(), msg);
        }
    }

    private async Task OnCallbackQuery(CallbackQuery callbackQuery)
    {
        var split = callbackQuery.Data!.Split(";");
        LogReceivedCallbackQueryCqcommand(logger, split[0]);
        switch (split[0])
        {
            case CallbackActions.Plan:
                {
                    LogReceivedPlanCommand(logger);
                    var date = DateTime.ParseExact(split[1], "dd/MM/yyyy", CultureInfo.InvariantCulture);
                    var userId = long.Parse(split[2]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, userId);
                    if (user is null) return;

                    await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                        new InlineKeyboardMarkup(keyboardGenerator.GenerateStatusKeyboard(date, userId)));

                    break;
                }
            case CallbackActions.PlanStatus:
                {
                    LogReceivedPlanCommand(logger);
                    var availability = int.Parse(split[1]);
                    var date = DateTime.ParseExact(split[2], "dd/MM/yyyy", CultureInfo.InvariantCulture);
                    var userId = long.Parse(split[3]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, userId);
                    if (user is null) return;

                    var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
                    var selectedAvailability = (Availability)availability;

                    if (selectedAvailability == Availability.Yes)
                    {
                        await bot.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                            text: "🕐 Назови час присоединения к грядущей битве",
                            replyMarkup: new InlineKeyboardMarkup(keyboardGenerator.GenerateTimeKeyboard(utcDate, userId)));
                    }
                    else
                    {
                        await availabilityManager.UpdateResponseForDate(callbackQuery.From, selectedAvailability, utcDate);
                        await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                            new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(userId)));
                    }

                    break;
                }
            case CallbackActions.PlanTime:
                {
                    LogReceivedPtimeCommand(logger);

                    var dateTime = DateTime.SpecifyKind(
                        DateTime.ParseExact(split[1], "dd/MM/yyyyTHH:mm", CultureInfo.InvariantCulture),
                        DateTimeKind.Utc
                    );
                    var userId = long.Parse(split[2]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, userId);
                    if (user is null) return;

                    var utcDateTime = timeZoneUtilities.ConvertToUtc(dateTime);

                    await availabilityManager.UpdateResponseForDate(callbackQuery.From, Availability.Yes, utcDateTime);

                    await bot.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                        text: "🗓️ Примени заклинание предсказания - объяви о свободных днях:",
                        replyMarkup: new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(userId)));

                    break;
                }
            case CallbackActions.PlanBack:
                {
                    LogReceivedPbackCommand(logger);
                    var userId = long.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, userId);
                    if (user is null) return;

                    await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                        new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(userId)));
                    break;
                }
            case CallbackActions.PlanDone:
                {
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    await availabilityManager.SetUnavailableForUnmarkedDays(callbackQuery.From.Username);

                    // Compute per-campaign free slots
                    var slotsPerCampaign = await slotCalculator.GetAvailableSlotsForAllCampaigns();

                    // Cache results — full replacement per campaign
                    var computedAt = DateTime.UtcNow;
                    foreach (var (campaign, slots) in slotsPerCampaign)
                    {
                        // Delete old cached slots for this campaign
                        await db.AvailableSlots
                            .Where(s => s.CampaignId == campaign.Id)
                            .ExecuteDeleteAsync();

                        // Insert new slots
                        foreach (var slot in slots)
                        {
                            await db.AvailableSlots.AddAsync(new AvailableSlot
                            {
                                CampaignId = campaign.Id,
                                DateTime = slot,
                                ComputedAt = computedAt
                            });
                        }
                    }

                    // Also clear cached slots for campaigns that no longer have any available slots
                    var campaignIdsWithSlots = slotsPerCampaign.Keys.Select(c => c.Id).ToHashSet();
                    var activeCampaignIds = await db.Campaigns
                        .Where(c => c.IsActive)
                        .Select(c => c.Id)
                        .ToListAsync();
                    foreach (var cid in activeCampaignIds.Where(id => !campaignIdsWithSlots.Contains(id)))
                    {
                        await db.AvailableSlots
                            .Where(s => s.CampaignId == cid)
                            .ExecuteDeleteAsync();
                    }

                    await db.SaveChangesAsync();

                    // Send summary message
                    if (slotsPerCampaign.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("📜 <b>Свободные слоты по кампаниям:</b>");
                        sb.AppendLine();

                        foreach (var (campaign, slots) in slotsPerCampaign)
                        {
                            var slotDescriptions = slots
                                .Select(s => timeZoneUtilities.ConvertToMoscow(s))
                                .Select(m => timeZoneUtilities.FormatDateTime(m));
                            sb.AppendLine(
                                $"<b>{campaign.ForumThread.Name}:</b> {string.Join(", ", slotDescriptions)}");
                        }

                        await bot.SendMessage(
                            callbackQuery.Message!.Chat.Id,
                            messageThreadId: callbackQuery.Message.MessageThreadId,
                            text: sb.ToString(),
                            parseMode: ParseMode.Html);
                    }
                    else
                    {
                        await bot.SendMessage(
                            callbackQuery.Message!.Chat.Id,
                            messageThreadId: callbackQuery.Message.MessageThreadId,
                            text: "📜 Свободных слотов пока нет — не все герои объявили о своей доступности.",
                            parseMode: ParseMode.Html);
                    }

                    break;
                }
            case CallbackActions.VoteCancel:
                {
                    var creatorUserId = long.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, creatorUserId);
                    if (user is null) return;

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

                    break;
                }
            case CallbackActions.CampaignJoin:
                {
                    var campaignId = int.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
                    if (user is null) return;

                    var joinError = await campaignManager.JoinCampaign(campaignId, user.Id);
                    var joinResultText = joinError ?? $"⚔️ {user.Name} вступает в ряды кампании!";

                    await bot.EditMessageText(
                        callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.Id,
                        joinResultText,
                        parseMode: ParseMode.Html);

                    break;
                }
            case CallbackActions.CampaignLeave:
                {
                    var campaignId = int.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
                    if (user is null) return;

                    var leaveError = await campaignManager.LeaveCampaign(campaignId, user.Id);
                    var leaveResultText = leaveError ?? $"👋 {user.Name} покидает ряды кампании.";

                    await bot.EditMessageText(
                        callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.Id,
                        leaveResultText,
                        parseMode: ParseMode.Html);

                    break;
                }
            case CallbackActions.CampaignDelete:
                {
                    var campaignId = int.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
                    if (user is null) return;

                    var deleteError = await campaignManager.DeleteCampaign(campaignId, user.Id);
                    var deleteResultText = deleteError ?? "📕 Кампания завершена — летопись запечатана.";

                    await bot.EditMessageText(
                        callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.Id,
                        deleteResultText,
                        parseMode: ParseMode.Html);

                    break;
                }
            case CallbackActions.StealCampaign:
                {
                    var campaignId = int.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
                    if (user is null) return;

                    // Verify DM ownership
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
                        break;
                    }

                    if (campaign.DungeonMasterId != user.Id)
                    {
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            "⚠️ Только Мастер Подземелий может призвать /steal!",
                            parseMode: ParseMode.Html);
                        break;
                    }

                    // Delete the campaign picker message and show slot picker
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    await commandHandler.ShowSlotPickerForCampaign(
                        callbackQuery.Message.Chat.Id,
                        callbackQuery.Message.MessageThreadId,
                        campaignId,
                        user.Id);

                    break;
                }
            case CallbackActions.StealSlot:
                {
                    var campaignId = int.Parse(split[1]);
                    var slotUtc = DateTime.SpecifyKind(
                        DateTime.ParseExact(split[2], "yyMMddHHmm", CultureInfo.InvariantCulture),
                        DateTimeKind.Utc);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[3]));
                    if (user is null) return;

                    // Load campaign with thread and members
                    var campaign = await db.Campaigns
                        .Include(c => c.ForumThread)
                        .Include(c => c.Members)
                        .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);

                    if (campaign is null)
                    {
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            "⚠️ Кампания не найдена или более не активна.",
                            parseMode: ParseMode.Html);
                        break;
                    }

                    if (campaign.DungeonMasterId != user.Id)
                    {
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            "⚠️ Только Мастер Подземелий может призвать /steal!",
                            parseMode: ParseMode.Html);
                        break;
                    }

                    // Build mentions from active campaign members
                    var memberUserIds = campaign.Members.Select(m => m.UserId).ToList();
                    var memberUsers = await db.Users
                        .Where(u => memberUserIds.Contains(u.Id) && u.IsActive)
                        .ToListAsync();

                    // Collision detection
                    var conflictingCampaigns = await commandHandler.GetConflictingCampaignNames(
                        campaignId, slotUtc, memberUsers.Select(u => u.Id).ToList());

                    if (conflictingCampaigns.Count > 0)
                    {
                        var conflictList = string.Join("\n", conflictingCampaigns.Select(n => $"  — {n}"));
                        var collisionKeyboard = keyboardGenerator.GenerateVoteCollisionKeyboard(campaignId, slotUtc, user.Id);
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            $"⚠️ Внимание, Мастер! В этот день уже записаны битвы в других кампаниях:\n\n{conflictList}\n\nНекоторые воины могут быть заняты. Продолжить голосование?",
                            parseMode: ParseMode.Html,
                            replyMarkup: new InlineKeyboardMarkup(collisionKeyboard));
                        break;
                    }

                    var activeMentions = string.Join(" ", memberUsers.Select(u => $"@{u.Username}"));

                    // Delete the slot picker message
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);

                    // Send voting message in the campaign's thread
                    await votingManager.SendVotingMessage(
                        campaign.ForumThread.ChatId,
                        campaign.ForumThread.ThreadId,
                        slotUtc,
                        user.Id,
                        activeMentions,
                        keyboardGenerator,
                        campaignId);

                    break;
                }
            case CallbackActions.VoteConfirm:
                {
                    var campaignId = int.Parse(split[1]);
                    var slotUtc = DateTime.SpecifyKind(
                        DateTime.ParseExact(split[2], "yyMMddHHmm", CultureInfo.InvariantCulture),
                        DateTimeKind.Utc);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[3]));
                    if (user is null) return;

                    var campaign = await db.Campaigns
                        .Include(c => c.ForumThread)
                        .Include(c => c.Members)
                        .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);

                    if (campaign is null)
                    {
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            "⚠️ Кампания не найдена или более не активна.",
                            parseMode: ParseMode.Html);
                        break;
                    }

                    if (campaign.DungeonMasterId != user.Id)
                    {
                        await bot.AnswerCallbackQuery(callbackQuery.Id,
                            "⚠️ Только Мастер может начать голосование!");
                        break;
                    }

                    // Build mentions
                    var memberUserIds = campaign.Members.Select(m => m.UserId).ToList();
                    var memberUsers = await db.Users
                        .Where(u => memberUserIds.Contains(u.Id) && u.IsActive)
                        .ToListAsync();
                    var activeMentions = string.Join(" ", memberUsers.Select(u => $"@{u.Username}"));

                    // Delete the collision warning message
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);

                    // Send voting message in the campaign's thread
                    await votingManager.SendVotingMessage(
                        campaign.ForumThread.ChatId,
                        campaign.ForumThread.ThreadId,
                        slotUtc,
                        user.Id,
                        activeMentions,
                        keyboardGenerator,
                        campaignId);

                    break;
                }
            case CallbackActions.VoteAbort:
                {
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[1]));
                    if (user is null) return;

                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    break;
                }
            case CallbackActions.UnsaveGame:
                {
                    var savedGameId = int.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
                    if (user is null) return;

                    // Verify DM rights
                    var game = await db.SavedGame
                        .Include(sg => sg.Campaign)
                        .FirstOrDefaultAsync(sg => sg.Id == savedGameId);

                    if (game is null)
                    {
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            "🔍 Битва уже вычеркнута из летописи.",
                            parseMode: ParseMode.Html);
                        break;
                    }

                    if (game.Campaign.DungeonMasterId != user.Id)
                    {
                        await bot.AnswerCallbackQuery(callbackQuery.Id,
                            "⚠️ Только Мастер может удалить запись о битве!");
                        break;
                    }

                    await commandHandler.DeleteSavedGameAndReminders(savedGameId);

                    await bot.EditMessageText(
                        callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.Id,
                        "🗡️ Битва вычеркнута из летописи.",
                        parseMode: ParseMode.Html);
                    break;
                }
            case CallbackActions.VoteCampaignPick:
                {
                    // DM picked a campaign from service-thread /vote → collision check, then fire vote
                    var campaignId = int.Parse(split[1]);
                    var slotUtc = DateTime.SpecifyKind(
                        DateTime.ParseExact(split[2], "yyMMddHHmm", CultureInfo.InvariantCulture),
                        DateTimeKind.Utc);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[3]));
                    if (user is null) return;

                    var campaign = await db.Campaigns
                        .Include(c => c.ForumThread)
                        .Include(c => c.Members)
                        .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);

                    if (campaign is null)
                    {
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            "⚠️ Кампания не найдена или более не активна.",
                            parseMode: ParseMode.Html);
                        break;
                    }

                    if (campaign.DungeonMasterId != user.Id)
                    {
                        await bot.AnswerCallbackQuery(callbackQuery.Id,
                            "⚠️ Только Мастер Подземелий может начать голосование!");
                        break;
                    }

                    // Build active member list
                    var memberUserIds = campaign.Members.Select(m => m.UserId).ToList();
                    var memberUsers = await db.Users
                        .Where(u => memberUserIds.Contains(u.Id) && u.IsActive)
                        .ToListAsync();

                    // Collision detection
                    var conflictingCampaigns = await commandHandler.GetConflictingCampaignNames(
                        campaignId, slotUtc, memberUsers.Select(u => u.Id).ToList());

                    if (conflictingCampaigns.Count > 0)
                    {
                        var conflictList = string.Join("\n", conflictingCampaigns.Select(n => $"  — {n}"));
                        var collisionKeyboard = keyboardGenerator.GenerateVoteCollisionKeyboard(campaignId, slotUtc, user.Id);
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            $"⚠️ Внимание, Мастер! В этот день уже записаны битвы в других кампаниях:\n\n{conflictList}\n\nНекоторые воины могут быть заняты. Продолжить голосование?",
                            parseMode: ParseMode.Html,
                            replyMarkup: new InlineKeyboardMarkup(collisionKeyboard));
                        break;
                    }

                    var activeMentions = string.Join(" ", memberUsers.Select(u => $"@{u.Username}"));
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);

                    await votingManager.SendVotingMessage(
                        campaign.ForumThread.ChatId,
                        campaign.ForumThread.ThreadId,
                        slotUtc,
                        user.Id,
                        activeMentions,
                        keyboardGenerator,
                        campaignId);

                    break;
                }
            case CallbackActions.SavedCampaignPick:
                {
                    // User picked a campaign from service-thread /saved → send saved games list
                    var campaignId = int.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
                    if (user is null) return;

                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    await commandHandler.SendSavedGamesForCampaign(
                        callbackQuery.Message.Chat.Id,
                        callbackQuery.Message.MessageThreadId,
                        campaignId);
                    break;
                }
            case CallbackActions.UnsaveCampaignPick:
                {
                    // DM picked a campaign from service-thread /unsave → show unsave keyboard
                    var campaignId = int.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, long.Parse(split[2]));
                    if (user is null) return;

                    var campaign = await db.Campaigns
                        .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);

                    if (campaign is null)
                    {
                        await bot.EditMessageText(
                            callbackQuery.Message!.Chat.Id,
                            callbackQuery.Message.Id,
                            "⚠️ Кампания не найдена или более не активна.",
                            parseMode: ParseMode.Html);
                        break;
                    }

                    if (campaign.DungeonMasterId != user.Id)
                    {
                        await bot.AnswerCallbackQuery(callbackQuery.Id,
                            "⚠️ Только Мастер Подземелий может стереть запись о битве!");
                        break;
                    }

                    await commandHandler.ShowUnsaveKeyboardForCampaign(
                        callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.MessageThreadId,
                        campaignId,
                        user.Id,
                        editMessageId: callbackQuery.Message.Id);
                    break;
                }
            case CallbackActions.Dismiss:
                {
                    var userId = long.Parse(split[1]);
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, userId);
                    if (user is null) return;

                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    break;
                }
        }

        await bot.AnswerCallbackQuery(callbackQuery.Id);
    }

    private async Task OnMessageReaction(MessageReactionUpdated reactionUpdated)
    {
        // Filter out bot's own reactions
        var me = await bot.GetMe();
        if (reactionUpdated.User?.Id == me.Id)
            return;

        // Detect thumbs-up changes
        var hasThumbsUpInNew = reactionUpdated.NewReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👍" });
        var hasThumbsUpInOld = reactionUpdated.OldReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👍" });

        // Detect thumbs-down changes
        var hasThumbsDownInNew = reactionUpdated.NewReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👎" });
        var hasThumbsDownInOld = reactionUpdated.OldReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👎" });

        var thumbsUpChanged = hasThumbsUpInNew != hasThumbsUpInOld;
        var thumbsDownChanged = hasThumbsDownInNew != hasThumbsDownInOld;

        // If neither reaction changed, ignore
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

        var outcome = VoteOutcome.Pending;

        // Process all removals first so duplicate-vote checks don't block additions
        // when a user switches reaction in a single update (e.g. 👎 → 👍).
        if (thumbsUpChanged && !hasThumbsUpInNew && hasThumbsUpInOld)
            await votingManager.RemoveVote(votingSession.Id, user.Id, VoteType.For);

        if (thumbsDownChanged && !hasThumbsDownInNew && hasThumbsDownInOld)
            await votingManager.RemoveVote(votingSession.Id, user.Id, VoteType.Against);

        // Then process additions
        if (thumbsUpChanged && hasThumbsUpInNew && !hasThumbsUpInOld)
        {
            outcome = await votingManager.RecordVoteAndCheckOutcome(
                votingSession.Id, user.Id, VoteType.For);
        }

        if (thumbsDownChanged && hasThumbsDownInNew && !hasThumbsDownInOld)
        {
            outcome = await votingManager.RecordVoteAndCheckOutcome(
                votingSession.Id, user.Id, VoteType.Against);
        }

        await HandleVoteOutcome(votingSession, outcome);
    }

    /// <summary>
    /// Handles the result of a vote: updates the message for pending votes,
    /// saves the game and closes the session when threshold is reached,
    /// or shows no-consensus message when too many against votes.
    /// </summary>
    private async Task HandleVoteOutcome(VoteSession votingSession, VoteOutcome outcome)
    {
        switch (outcome)
        {
            case VoteOutcome.Saved:
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
                    break;
                }
            case VoteOutcome.NoConsensus:
                {
                    await votingManager.CloseVotingSession(votingSession.Id, VoteOutcome.NoConsensus);
                    await votingManager.DeleteVotingSession(votingSession.Id);

                    await bot.EditMessageText(
                        votingSession.ChatId,
                        votingSession.MessageId,
                        "⚡ Совет не достиг согласия — слишком много голосов против. Битва не состоится в этот час.",
                        parseMode: ParseMode.Html);
                    break;
                }
            default: // Pending — update vote counts display
                {
                    var updatedSession = await votingManager.GetVotingSession(votingSession.Id);
                    if (updatedSession is not null)
                    {
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
                            // Message content unchanged — Telegram API throws if text is identical
                        }
                        catch (ApiRequestException ex)
                        {
                            logger.LogWarning(ex, "Failed to update voting message");
                        }
                    }

                    break;
                }
        }
    }

    private async Task OnCommand(string command, string args, Message msg)
    {
        LogReceivedCommandCommandArgs(logger, command, args);
        await commandHandler.ExecuteCommand(command, args, msg);
    }

    private Task UnknownUpdateHandlerAsync(Update update)
    {
        LogUnknownUpdateTypeUpdatetype(logger, update.Type);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates callback ownership by looking up the user by DB ID and comparing
    /// their username to the callback sender. Returns null if validation fails.
    /// </summary>
    private async Task<Data.User?> ValidateCallbackOwnerAndResolveUser(
        CallbackQuery callbackQuery, long userId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "⚠️ Сначала зарегистрируйся командой /unpause");
            return null;
        }

        if (user.Username != callbackQuery.From.Username)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "🚨 Эта кнопка защищена древним проклятием!");
            return null;
        }

        return user;
    }
}
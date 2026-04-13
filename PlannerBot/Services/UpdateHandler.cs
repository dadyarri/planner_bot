using System.Globalization;
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
        LogHandleerrorException(logger);
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
                    var username = split[2];

                    if (username != callbackQuery.From.Username)
                    {
                        LogWrongUserUsedPlanCommand(logger, username, callbackQuery.From.Username!);
                        await bot.AnswerCallbackQuery(callbackQuery.Id, "🚨 Эта кнопка защищена древним проклятием!");
                        return;
                    }

                    await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                        new InlineKeyboardMarkup(keyboardGenerator.GenerateStatusKeyboard(date, username)));

                    break;
                }
            case CallbackActions.PlanStatus:
                {
                    LogReceivedPlanCommand(logger);
                    var availability = int.Parse(split[1]);
                    var date = DateTime.ParseExact(split[2], "dd/MM/yyyy", CultureInfo.InvariantCulture);
                    var username = split[3];

                    if (username != callbackQuery.From.Username)
                    {
                        LogWrongUserUsedPlanCommand(logger, username, callbackQuery.From.Username!);
                        await bot.AnswerCallbackQuery(callbackQuery.Id, "🚨 Эта кнопка защищена древним проклятием!");
                        return;
                    }

                    var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
                    var selectedAvailability = (Availability)availability;

                    if (selectedAvailability == Availability.Yes)
                    {
                        await bot.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                            text: "🕐 Назови час присоединения к грядущей битве",
                            replyMarkup: new InlineKeyboardMarkup(keyboardGenerator.GenerateTimeKeyboard(utcDate, username)));
                    }
                    else
                    {
                        await availabilityManager.UpdateResponseForDate(callbackQuery.From, selectedAvailability, utcDate);
                        await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                            new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(username)));
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
                    var username = split[2];

                    if (username != callbackQuery.From.Username)
                    {
                        LogWrongUserUsedPtimeButtonDataCq(logger, username, callbackQuery.From.Username!);
                        await bot.AnswerCallbackQuery(callbackQuery.Id, "🚨 Эта кнопка защищена древним проклятием!");
                        return;
                    }

                    var utcDateTime = timeZoneUtilities.ConvertToUtc(dateTime);

                    await availabilityManager.UpdateResponseForDate(callbackQuery.From, Availability.Yes, utcDateTime);

                    await bot.EditMessageText(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                        text: "🗓️ Примени заклинание предсказания - объяви о свободных днях:",
                        replyMarkup: new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(username)));

                    break;
                }
            case CallbackActions.PlanBack:
                {
                    LogReceivedPbackCommand(logger);
                    var username = split[1];

                    if (username != callbackQuery.From.Username)
                    {
                        await bot.AnswerCallbackQuery(callbackQuery.Id, "🚨 Эта кнопка защищена древним проклятием!");
                        return;
                    }

                    await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                        new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(username)));
                    break;
                }
            case CallbackActions.PlanDone:
                {
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    await availabilityManager.SetUnavailableForUnmarkedDays(callbackQuery.From.Username);

                    var now = DateTime.UtcNow;
                    var activeUsers = await db.Users.Where(u => u.IsActive).ToListAsync();
                    var activeMentions = string.Join(" ", activeUsers.Select(u => $"@{u.Username}"));

                    for (var i = 0; i < 8; i++)
                    {
                        var date = now.AddDays(i).Date;
                        var suitableTime = await slotCalculator.CheckIfDateIsAvailable(date);

                        if (suitableTime is not null)
                        {
                            // suitableTime is already in Moscow time from CheckIfDateIsAvailable
                            var moscowGameDateTime = suitableTime.Value;
                            var utcGameDateTime = timeZoneUtilities.ConvertToUtc(moscowGameDateTime);

                            await votingManager.SendVotingMessage(
                                callbackQuery.Message!.Chat.Id,
                                callbackQuery.Message.MessageThreadId,
                                utcGameDateTime,
                                callbackQuery.From.Username!,
                                activeMentions,
                                keyboardGenerator);
                        }
                    }

                    break;
                }
            case CallbackActions.VoteCancel:
                {
                    var creatorUsername = split[1];

                    if (creatorUsername != callbackQuery.From.Username)
                    {
                        await bot.AnswerCallbackQuery(callbackQuery.Id,
                            "🚨 Только создатель голосования может его отменить!");
                        return;
                    }

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
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, split[2]);
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
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, split[2]);
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
                    var user = await ValidateCallbackOwnerAndResolveUser(callbackQuery, split[2]);
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

        // Process thumbs-up vote
        if (thumbsUpChanged)
        {
            if (hasThumbsUpInNew && !hasThumbsUpInOld)
            {
                outcome = await votingManager.RecordVoteAndCheckOutcome(
                    votingSession.Id, user.Id, VoteType.For);
            }
            else if (!hasThumbsUpInNew && hasThumbsUpInOld)
            {
                await votingManager.RemoveVote(votingSession.Id, user.Id, VoteType.For);
            }
        }

        // Process thumbs-down vote
        if (thumbsDownChanged)
        {
            if (hasThumbsDownInNew && !hasThumbsDownInOld)
            {
                outcome = await votingManager.RecordVoteAndCheckOutcome(
                    votingSession.Id, user.Id, VoteType.Against);
            }
            else if (!hasThumbsDownInNew && hasThumbsDownInOld)
            {
                await votingManager.RemoveVote(votingSession.Id, user.Id, VoteType.Against);
            }
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

                    await gameScheduler.SavePlannedGame(votingSession.GameDateTime, messageInfo);
                    await votingManager.DeleteVotingSession(votingSession.Id);

                    await bot.EditMessageText(
                        votingSession.ChatId,
                        votingSession.MessageId,
                        "✅ Совет единогласен — битва записана в летописи! ⚔️",
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
                                    keyboardGenerator.GenerateVoteCancelKeyboard(updatedSession.CreatorUsername)));
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
    /// Validates callback ownership and resolves the user from the database.
    /// Returns null and sends an appropriate callback answer if validation fails.
    /// </summary>
    private async Task<Data.User?> ValidateCallbackOwnerAndResolveUser(
        CallbackQuery callbackQuery, string expectedUsername)
    {
        if (expectedUsername != callbackQuery.From.Username)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "🚨 Эта кнопка защищена древним проклятием!");
            return null;
        }

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Username == callbackQuery.From.Username);

        if (user is null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id,
                "⚠️ Сначала зарегистрируйся командой /unpause");
        }

        return user;
    }
}
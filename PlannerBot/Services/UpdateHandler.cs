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
    CommandHandler commandHandler,
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
            case "plan":
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
            case "pstatus":
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
            case "ptime":
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
            case "pback":
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
            case "delete":
                {
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    await availabilityManager.SetUnavailableForUnmarkedDays(callbackQuery.From.Username);

                    var now = DateTime.UtcNow;
                    var activeUsers = await db.Users.Where(u => u.IsActive).ToListAsync();
                    var activeMentions = string.Join(" ", activeUsers.Select(u => $"@{u.Username}"));

                    for (var i = 0; i < 8; i++)
                    {
                        var date = now.AddDays(i).Date;
                        var suitableTime = await availabilityManager.CheckIfDateIsAvailable(date);

                        if (suitableTime is not null)
                        {
                            // suitableTime is already in Moscow time from CheckIfDateIsAvailable
                            var moscowGameDateTime = suitableTime.Value;
                            var utcGameDateTime = timeZoneUtilities.ConvertToUtc(moscowGameDateTime);

                            await availabilityManager.SendVotingMessage(
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
            case "vote_cancel":
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
                        await availabilityManager.CloseVotingSession(voteSession.Id, VoteOutcome.Canceled);
                        await availabilityManager.DeleteVotingSession(voteSession.Id);
                    }

                    await bot.EditMessageText(
                        callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.Id,
                        "🛑 Голосование отменено создателем — совет распущен",
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
                outcome = await availabilityManager.RecordVoteAndCheckOutcome(
                    votingSession.Id, user.Id, VoteType.For);
            }
            else if (!hasThumbsUpInNew && hasThumbsUpInOld)
            {
                await availabilityManager.RemoveVote(votingSession.Id, user.Id, VoteType.For);
            }
        }

        // Process thumbs-down vote
        if (thumbsDownChanged)
        {
            if (hasThumbsDownInNew && !hasThumbsDownInOld)
            {
                outcome = await availabilityManager.RecordVoteAndCheckOutcome(
                    votingSession.Id, user.Id, VoteType.Against);
            }
            else if (!hasThumbsDownInNew && hasThumbsDownInOld)
            {
                await availabilityManager.RemoveVote(votingSession.Id, user.Id, VoteType.Against);
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
                    await availabilityManager.CloseVotingSession(votingSession.Id, VoteOutcome.Saved);

                    var messageInfo = new Message
                    {
                        Chat = new Chat { Id = votingSession.ChatId },
                        Id = votingSession.MessageId,
                        MessageThreadId = votingSession.ThreadId
                    };

                    await availabilityManager.SavePlannedGame(votingSession.GameDateTime, messageInfo);
                    await availabilityManager.DeleteVotingSession(votingSession.Id);

                    await bot.EditMessageText(
                        votingSession.ChatId,
                        votingSession.MessageId,
                        "✅ Совет единогласен — битва записана в летописи! ⚔️",
                        parseMode: ParseMode.Html);
                    break;
                }
            case VoteOutcome.NoConsensus:
                {
                    await availabilityManager.CloseVotingSession(votingSession.Id, VoteOutcome.NoConsensus);
                    await availabilityManager.DeleteVotingSession(votingSession.Id);

                    await bot.EditMessageText(
                        votingSession.ChatId,
                        votingSession.MessageId,
                        "⚡ Совет не достиг согласия — слишком много голосов против. Битва не состоится в этот час.",
                        parseMode: ParseMode.Html);
                    break;
                }
            default: // Pending — update vote counts display
                {
                    var updatedSession = await availabilityManager.GetVotingSession(votingSession.Id);
                    if (updatedSession is not null)
                    {
                        var messageText = await availabilityManager.BuildVotingMessageText(updatedSession);

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
                        catch (ApiRequestException)
                        {
                            // Message content unchanged — Telegram API throws if text is identical
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
}
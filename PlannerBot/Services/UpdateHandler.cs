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
                            date = date.Add(suitableTime.Value.TimeOfDay);
                            var sentMessage = await bot.SendMessage(callbackQuery.Message!.Chat.Id,
                                messageThreadId: callbackQuery.Message.MessageThreadId,
                                text:
                                $"⭐ Судьба совпала! {timeZoneUtilities.FormatDate(date)} братство объединено! Час кампании: <b>{timeZoneUtilities.FormatTime(date)}</b>\n\n👍 Голосуй за запись битвы в летописи!\n\n{activeMentions}",
                                parseMode: ParseMode.Html, linkPreviewOptions: true);

                            // Create voting session and store message ID
                            var votingMessage = await availabilityManager.CreateVotingSession(timeZoneUtilities.ConvertToUtc(date), sentMessage);
                            if (votingMessage is not null)
                            {
                                votingMessage.MessageId = sentMessage.MessageId;
                                await db.SaveChangesAsync();

                                await bot.SetMessageReaction(
                                    callbackQuery.Message.Chat.Id,
                                    sentMessage.MessageId,
                                    [new ReactionTypeEmoji { Emoji = "👍" }]);
                            }
                        }
                    }

                    break;
                }
        }

        await bot.AnswerCallbackQuery(callbackQuery.Id);
    }

    private async Task OnMessageReaction(MessageReactionUpdated reactionUpdated)
    {
        var hasThumbsUpInNew = reactionUpdated.NewReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👍" });
        var hasThumbsUpInOld = reactionUpdated.OldReaction.Any(r => r is ReactionTypeEmoji { Emoji: "👍" });

        // If reaction wasn't added or removed, ignore
        if (hasThumbsUpInNew == hasThumbsUpInOld)
            return;

        var votingMessage = await db.VoteSessions
            .FirstOrDefaultAsync(vm =>
                vm.ChatId == reactionUpdated.Chat.Id &&
                vm.MessageId == reactionUpdated.MessageId);

        if (votingMessage is null)
            return;

        var user = await db.Users.FirstOrDefaultAsync(u => reactionUpdated.User != null && u.Username == reactionUpdated.User.Username);
        if (user is null || !user.IsActive)
            return;

        bool thresholdReached = false;

        // Reaction was added
        if (hasThumbsUpInNew && !hasThumbsUpInOld)
        {
            thresholdReached = await availabilityManager.IncrementVoteAndCheckThreshold(votingMessage.Id);
        }
        // Reaction was removed
        else if (!hasThumbsUpInNew && hasThumbsUpInOld)
        {
            await availabilityManager.DecrementVote(votingMessage.Id);
        }

        var activeUsersCount = await db.Users.Where(u => u.IsActive).CountAsync();
        var updatedVotingMessage = await availabilityManager.GetVotingMessage(votingMessage.Id);

        if (updatedVotingMessage is not null)
        {
            var moscowGameDateTime = timeZoneUtilities.ConvertToMoscow(updatedVotingMessage.GameDateTime);
            await bot.EditMessageText(
                votingMessage.ChatId,
                votingMessage.MessageId,
                $"⭐ Судьба совпала! {timeZoneUtilities.FormatDate(moscowGameDateTime)} братство объединено! Час кампании: <b>{timeZoneUtilities.FormatTime(moscowGameDateTime)}</b>\n\n👍 Голосов: {updatedVotingMessage.VoteCount}/{activeUsersCount}",
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
        }

        if (thresholdReached)
        {
            var messageInfo = new Message
            {
                Chat = new Chat { Id = votingMessage.ChatId },
                Id = votingMessage.MessageId,
                MessageThreadId = votingMessage.ThreadId
            };

            await availabilityManager.SavePlannedGame(votingMessage.GameDateTime, messageInfo);
            await availabilityManager.DeleteVotingSession(votingMessage.Id);

            await bot.DeleteMessage(votingMessage.ChatId, votingMessage.MessageId);
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
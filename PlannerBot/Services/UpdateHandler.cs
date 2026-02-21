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
    AppDbContext db,
    TimeZoneUtilities timeZoneUtilities,
    KeyboardGenerator keyboardGenerator,
    AvailabilityManager availabilityManager,
    CommandHandler commandHandler) : IUpdateHandler
{

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await (update switch
        {
            { Message: { } message } => OnMessage(message),
            { CallbackQuery: { } callbackQuery } => OnCallbackQuery(callbackQuery),
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
                var availability = int.Parse(split[1]);
                var data = new PlanButtonCallback
                {
                    Availability = (Availability)availability,
                    Date = DateTime.ParseExact(split[2], "dd/MM/yyyy", CultureInfo.InvariantCulture),
                    Username = split[3],
                };
                var newAvailability = (Availability)((int)(data.Availability + 1) % 4);

                if (data.Username != callbackQuery.From.Username)
                {
                    LogWrongUserUsedPlanCommand(logger, data.Username, callbackQuery.From.Username!);
                    await bot.AnswerCallbackQuery(callbackQuery.Id, "Не твоя кнопка!");
                    return;
                }

                var date = DateTime.SpecifyKind(data.Date.Date, DateTimeKind.Utc);
                await availabilityManager.UpdateResponseForDate(callbackQuery.From, newAvailability, date);

                if (newAvailability == Availability.Yes)
                {
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    await bot.SendMessage(callbackQuery.Message!.Chat.Id,
                        messageThreadId: callbackQuery.Message!.MessageThreadId,
                        text: "Выбери время, начиная с которого ты свободен",
                        replyMarkup: new InlineKeyboardMarkup(keyboardGenerator.GenerateTimeKeyboard(date, data.Username)));
                }
                else
                {
                    await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.Id,
                        new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(data.Username)));
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
                    await bot.AnswerCallbackQuery(callbackQuery.Id, "Не твоя кнопка!");
                    return;
                }

                var utcDateTime = timeZoneUtilities.ConvertToUtc(dateTime);

                var response = await db.Responses
                    .Include(r => r.User)
                    .Where(r => r.User.Username == username &&
                                r.DateTime.HasValue && r.DateTime.Value.Date == utcDateTime.Date)
                    .FirstOrDefaultAsync();

                response?.DateTime = utcDateTime;
                await db.SaveChangesAsync();

                await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                await bot.SendMessage(callbackQuery.Message!.Chat.Id,
                    messageThreadId: callbackQuery.Message!.MessageThreadId,
                    text: "Здесь можно настроить свободные дни в ближайшее время:",
                    replyMarkup: new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(username)));

                break;
            }
            case "pback":
            {
                LogReceivedPbackCommand(logger);
                var username = split[1];

                if (username != callbackQuery.From.Username)
                {
                    await bot.AnswerCallbackQuery(callbackQuery.Id, "Не твоя кнопка!");
                    return;
                }

                await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id,
                    new InlineKeyboardMarkup(await keyboardGenerator.GeneratePlanKeyboard(username)));
                break;
            }
            case "save":
            {
                var culture = timeZoneUtilities.GetRussianCultureInfo();
                var dateTime = DateTime.ParseExact($"{split[1]};{split[2]}", "dd/MM/yyyy;HH:mm", culture);

                await availabilityManager.SavePlannedGame(dateTime, callbackQuery.Message!, logger);

                break;
            }
            case "delete":
            {
                await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);

                var now = DateTime.UtcNow;
                for (var i = 0; i < 8; i++)
                {
                    var date = now.AddDays(i).Date;
                    var suitableTime = await availabilityManager.CheckIfDateIsAvailable(date);

                    if (suitableTime is not null)
                    {
                        date = date.Add(suitableTime.Value.TimeOfDay);
                        await bot.SendMessage(callbackQuery.Message!.Chat.Id,
                            messageThreadId: callbackQuery.Message.MessageThreadId,
                            text:
                            $"Ура! {date:dd.MM.yyyy} все могут! Удобное время: <b>{date:HH:mm}</b>",
                            parseMode: ParseMode.Html, linkPreviewOptions: true,
                            replyMarkup: new InlineKeyboardMarkup(
                                InlineKeyboardButton.WithCallbackData("Сохранить",
                                    $"save;{date:dd/MM/yyyy;HH:mm}")
                            )
                        );
                    }
                }

                break;
            }
        }

        await bot.AnswerCallbackQuery(callbackQuery.Id);
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
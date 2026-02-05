using System.Diagnostics;
using System.Globalization;
using System.Text;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Background;
using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;
using User = PlannerBot.Data.User;

namespace PlannerBot.Services;

public partial class UpdateHandler(
    ITelegramBotClient bot,
    ILogger<UpdateHandler> logger,
    AppDbContext db,
    ITimeTickerManager<TimeTickerEntity> ticker) : IUpdateHandler
{
    private readonly CultureInfo _russianCultureInfo = new("ru-RU");

    private static readonly TimeSpan[] ReminderIntervals =
    [
        TimeSpan.FromHours(48), TimeSpan.FromHours(24), TimeSpan.FromHours(5), TimeSpan.FromHours(3),
        TimeSpan.FromHours(1),
        TimeSpan.FromMinutes(10)
    ];

    private static readonly TimeZoneInfo MoscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private DateTime ConvertToUtc(DateTime localTime)
    {
        if (localTime.Kind == DateTimeKind.Utc)
            return localTime;
        return TimeZoneInfo.ConvertTimeToUtc(localTime, MoscowTimeZone);
    }

    private DateTime ConvertToMoscow(DateTime utcTime)
    {
        if (utcTime.Kind != DateTimeKind.Utc)
            utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTime(utcTime, MoscowTimeZone);
    }

    private DateTime GetMoscowDate()
    {
        var moscowNow = ConvertToMoscow(DateTime.UtcNow);
        return moscowNow.Date;
    }

    private DateTime GetMoscowDateTime()
    {
        return ConvertToMoscow(DateTime.UtcNow);
    }

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
                await UpdateResponseForDate(callbackQuery.From, newAvailability, date);

                if (newAvailability == Availability.Yes)
                {
                    await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);
                    await bot.SendMessage(callbackQuery.Message!.Chat.Id,
                        messageThreadId: callbackQuery.Message!.MessageThreadId,
                        text: "Выбери время, начиная с которого ты свободен",
                        replyMarkup: GenerateTimeKeyboard(date, data.Username));
                }
                else
                {
                    await bot.EditMessageReplyMarkup(callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.Id,
                        await GeneratePlanKeyboard(callbackQuery.Message, data.Username));
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

                var utcDateTime = ConvertToUtc(dateTime);

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
                    replyMarkup: await GeneratePlanKeyboard(callbackQuery.Message!, username));

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
                    await GeneratePlanKeyboard(callbackQuery.Message!, username));
                break;
            }
            case "save":
            {
                var dateTime = DateTime.ParseExact($"{split[1]};{split[2]}", "dd/MM/yyyy;HH:mm",
                    _russianCultureInfo);

                await SavePlannedGame(dateTime, callbackQuery.Message!);

                break;
            }
            case "delete":
            {
                await bot.DeleteMessage(callbackQuery.Message!.Chat.Id, callbackQuery.Message.Id);

                var moscowNow = GetMoscowDateTime();
                for (var i = 0; i < 8; i++)
                {
                    var date = moscowNow.AddDays(i).Date;
                    var suitableTime = await CheckIfDateIsAvailable(date);

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
        switch (command)
        {
            case "/start":
            {
                await SendUsage(msg);
                break;
            }
            case "/yes":
            {
                await HandleYesCommand(msg, args);
                break;
            }
            case "/no":
            {
                await HandleNoCommand(msg);
                break;
            }
            case "/prob":
            {
                await HandleProbablyCommand(msg);
                break;
            }
            case "/get":
            {
                await HandleGetCommand(msg);
                break;
            }
            case "/pause":
            {
                await HandlePauseCommand(msg);
                break;
            }
            case "/unpause":
            {
                await HandleUnpauseCommand(msg);
                break;
            }
            case "/plan":
            {
                await HandlePlanCommand(msg);
                break;
            }
            case "/set":
            {
                await HandleSetCommand(msg, args);
                break;
            }
        }
    }

    private async Task SendUsage(Message msg)
    {
        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId, text: """
                <b><u>Меню бота</u></b>:
                /yes hh:mm - Указать, что могу играть сегодня (с указанием времени)
                /no - Указать, что не могу играть сегодня
                /prob - Указать, что возможно могу сегодня
                /plan - Запланировать на 8 дней

                /pause - Приостановить участие в играх
                /unpause - Восстановить участие в играх

                /get - Показать общий план и ближайшее пересечение
                /set dd.mm.yyyy hh:mm - Установить время ближайшей игры
                """, parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new ReplyKeyboardRemove());
    }

    private async Task HandleYesCommand(Message msg, string? args = null)
    {
        if (string.IsNullOrEmpty(args))
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "Укажи время, начиная с которого ты свободен (любое, кроме 00:00)",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());
            return;
        }

        var suitableTime =
            await UpdateResponseForDate(msg.From!, Availability.Yes, GetMoscowDate(),
                args);
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["❤"]);

        if (suitableTime is not null)
        {
            var today = GetMoscowDate().Add(suitableTime.Value.TimeOfDay);
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: $"Ура! Сегодня все могут! Удобное время: <b>{today:HH:mm}</b>",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(
                    InlineKeyboardButton.WithCallbackData("Сохранить",
                        $"save;{today:dd/MM/yyyy;HH:mm}")
                )
            );
        }
    }

    private async Task HandleNoCommand(Message msg)
    {
        await UpdateResponseForDate(msg.From!, Availability.No, GetMoscowDate());

        var now = DateTime.UtcNow;
        var savedGamesForToday = await db.SavedGame
            .Where(sg => sg.DateTime.Date == now.Date)
            .ToListAsync();

        foreach (var savedGame in savedGamesForToday)
        {
            var jobIds = await db.Set<TimeTickerEntity>()
                .Where(t => t.ExecutionTime!.Value.Date == savedGame.DateTime)
                .Select(t => t.Id)
                .ToListAsync();

            await ticker.DeleteBatchAsync(jobIds);
        }

        if (savedGamesForToday.Count != 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "Сегодняшняя игра была отменена",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());
        }

        await bot.SetMessageReaction(msg.Chat, msg.Id, ["💩"]);
    }

    private async Task HandleProbablyCommand(Message msg)
    {
        await UpdateResponseForDate(msg.From!, Availability.Probably, GetMoscowDate());
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["😐"]);
    }

    private async Task HandleGetCommand(Message msg)
    {
        var users = await db.Users
            .Where(u => u.IsActive)
            .ToListAsync();

        var usernames = users.Select(u => u.Username).ToList();

        var now = DateTime.UtcNow;
        var today = now.Date;
        var end = now.AddDays(6).Date;

        var sb = new StringBuilder();

        for (var i = 0; i < 7; i++)
        {
            var date = now.AddDays(i).Date;
            sb.AppendLine($"<b>{date.ToString("dd MMM (ddd)", _russianCultureInfo)}</b>");
            sb.AppendLine();

            foreach (var user in users)
            {
                var responseDateTime = await db.Responses
                    .Where(r => r.DateTime.HasValue && r.DateTime.Value.Date == date &&
                                r.User.Username == user.Username)
                    .Select(r => r.DateTime)
                    .FirstOrDefaultAsync();

                var response = await db.Responses
                    .Where(r => r.DateTime == responseDateTime && r.User.Username == user.Username)
                    .FirstOrDefaultAsync();

                var time = string.Empty;

                if (response is { Availability: Availability.Yes, DateTime: not null } &&
                    response.DateTime.Value.TimeOfDay != TimeSpan.Zero)
                {
                    var moscowTime = ConvertToMoscow(response.DateTime.Value);
                    time = $" (с {moscowTime:HH:mm})";
                }

                sb.AppendLine(
                    $"{user.Name}: <i>{(response?.Availability ?? Availability.Unknown).ToSign()}{time}</i>");
            }

            sb.AppendLine();
        }

        var nearestFittingDate = await db.Responses
            .Include(v => v.User)
            .Where(v => v.DateTime.HasValue &&
                        v.DateTime.Value.Date >= today &&
                        v.DateTime.Value.Date <= end &&
                        usernames.Contains(v.User.Username) && v.User.IsActive)
            .GroupBy(v => v.DateTime!.Value.Date)
            .Where(g =>
                g.Count() == usernames.Count && // все пользователи проголосовали
                g.All(v => v.Availability != Availability.No)) // нет отказов
            .OrderBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefaultAsync();

        var availableTime = await CheckIfDateIsAvailable(nearestFittingDate);

        var formattedDate = nearestFittingDate.ToString("dd MMM (ddd)", _russianCultureInfo);
        var formattedTime = availableTime.HasValue ? availableTime.Value.ToString("hh:mm") : string.Empty;
        var format = nearestFittingDate != default
            ? $"{formattedDate} {formattedTime}"
            : "не найдено";

        sb.Append($"<b>Ближайшая удобная дата</b>: {format}");

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId, text: sb.ToString(),
            parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new ReplyKeyboardRemove());
    }

    private async Task HandlePauseCommand(Message msg)
    {
        var user = await db.Users.Where(u => u.Username == msg.From!.Username)
            .FirstOrDefaultAsync();

        if (user is null)
        {
            user = new User
            {
                Username = msg.From!.Username ?? throw new UnreachableException(),
                Name = $"{msg.From!.FirstName} {msg.From!.LastName}".Trim(),
                IsActive = false
            };
            await db.Users.AddAsync(user);
        }

        user.IsActive = false;
        await db.SaveChangesAsync();
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["😢"]);
    }

    private async Task HandleUnpauseCommand(Message msg)
    {
        var user = await db.Users.Where(u => u.Username == msg.From!.Username)
            .FirstOrDefaultAsync();

        if (user is null)
        {
            user = new User
            {
                Username = msg.From!.Username ?? throw new UnreachableException(),
                Name = $"{msg.From!.FirstName} {msg.From!.LastName}".Trim(),
                IsActive = true
            };
            await db.Users.AddAsync(user);
        }

        user.IsActive = true;
        await db.SaveChangesAsync();
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["🎉"]);
    }

    private async Task HandlePlanCommand(Message msg)
    {
        var calendar = await GeneratePlanKeyboard(msg);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "Здесь можно настроить свободные дни в ближайшее время:", parseMode: ParseMode.Html,
            linkPreviewOptions: true,
            replyMarkup: calendar);
    }

    private async Task HandleSetCommand(Message msg, string args)
    {
        if (args == string.Empty)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: """
                      Пропущены аргументы с датой/временем.

                      Пример использования:
                      /set 28.01.2026 18:30
                      """, parseMode: ParseMode.Html,
                linkPreviewOptions: true);
        }

        if (!DateTime.TryParseExact(args, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: """
                      Невалидный формат даты/времени.

                      Пример использования:
                      /set 28.01.2026 18:30
                      """, parseMode: ParseMode.Html,
                linkPreviewOptions: true);
            return;
        }

        await SavePlannedGame(date, msg);
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["🔥"]);
    }

    private Task UnknownUpdateHandlerAsync(Update update)
    {
        LogUnknownUpdateTypeUpdatetype(logger, update.Type);
        return Task.CompletedTask;
    }


    async Task<InlineKeyboardButton[][]> GeneratePlanKeyboard(Message message,
        string? username = null)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;

        const int weeks = 2;
        const int daysInWeek = 4;

        var inlineKeyboardButtons = new InlineKeyboardButton[weeks + 1][];

        for (var w = 0; w < weeks; w++)
        {
            inlineKeyboardButtons[w] = new InlineKeyboardButton[daysInWeek];
            for (var d = 0; d < daysInWeek; d++)
            {
                var offset = w * daysInWeek + d;
                var date = today.AddDays(offset);

                var availability = await db.Responses
                    .Include(r => r.User)
                    .Where(r => r.User.Username == (username ?? message.From!.Username) &&
                                r.DateTime.HasValue && r.DateTime.Value.Date == date)
                    .Select(r => r.Availability)
                    .FirstOrDefaultAsync();

                var emoji = availability switch
                {
                    Availability.Yes => "✅ ",
                    Availability.No => "❌ ",
                    Availability.Probably => "❓ ",
                    _ => string.Empty
                };

                var format = date.ToString("dd.MM (ddd)", _russianCultureInfo);
                inlineKeyboardButtons[w][d] = InlineKeyboardButton.WithCallbackData(
                    $"{emoji}{format}",
                    $"plan;{(int)(availability ?? Availability.Unknown)};{date:dd/MM/yyyy};{username ?? message.From!.Username}"
                );
            }
        }

        inlineKeyboardButtons[2] =
        [
            InlineKeyboardButton.WithCallbackData(
                "Закончить",
                "delete"
            )
        ];

        return inlineKeyboardButtons;
    }

    async Task<DateTime?> UpdateResponseForDate(Telegram.Bot.Types.User from, Availability availability, DateTime date,
        string? args = null)
    {
        var time = TimeSpan.Zero;
        if (args is not null)
        {
            var dt = DateTime.ParseExact(args, "HH:mm", _russianCultureInfo);
            time = dt.TimeOfDay;
        }

        var dateTime = date.Add(time);
        var utcDateTime = ConvertToUtc(dateTime);

        var response = await db.Responses.Where(r =>
                r.User.Username == from.Username &&
                r.DateTime.HasValue && r.DateTime.Value.Date == utcDateTime.Date)
            .FirstOrDefaultAsync();

        if (response is not null)
        {
            response.Availability = availability;
            response.DateTime = utcDateTime;
        }
        else
        {
            var user = await db.Users.Where(u => u.Username == from.Username)
                .FirstOrDefaultAsync();

            if (user is null)
            {
                user = new User
                {
                    Username = from.Username ?? throw new UnreachableException(),
                    Name = $"{from.FirstName} {from.LastName}".Trim(),
                    IsActive = true
                };
                await db.Users.AddAsync(user);
            }

            response = new Response
            {
                Availability = availability,
                DateTime = utcDateTime,
                User = user
            };
            await db.Responses.AddAsync(response);
        }

        await db.SaveChangesAsync();

        var suitableTime = await CheckIfDateIsAvailable(utcDateTime);
        return suitableTime;
    }

    InlineKeyboardButton[][] GenerateTimeKeyboard(
        DateTime date,
        string? username = null)
    {
        var start = new TimeSpan(6, 0, 0);
        var end = new TimeSpan(17, 30, 0);
        var step = TimeSpan.FromMinutes(30);

        const int slotsPerRow = 4;

        date = date.Add(start);

        var buttons = new List<InlineKeyboardButton[]>();
        var currentRow = new List<InlineKeyboardButton>();

        for (var dt = date; dt.TimeOfDay <= end; dt = dt.Add(step))
        {
            var localDt = ConvertToMoscow(dt);
            currentRow.Add(
                InlineKeyboardButton.WithCallbackData(
                    localDt.ToString("HH:mm"),
                    $"ptime;{dt:dd/MM/yyyyTHH:mm};{username}"
                )
            );

            if (currentRow.Count != slotsPerRow) continue;
            buttons.Add(currentRow.ToArray());
            currentRow.Clear();
        }

        if (currentRow.Count > 0)
            buttons.Add(currentRow.ToArray());

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "Назад",
                $"pback;{username}"
            )
        ]);

        return buttons.ToArray();
    }

    async Task<DateTime?> CheckIfDateIsAvailable(DateTime date)
    {
        var activeUsersCount = await db.Users
            .Where(u => u.IsActive)
            .CountAsync();

        var responses = await db.Responses
            .Where(r =>
                r.DateTime.HasValue && r.DateTime.Value.Date == date.Date &&
                r.User.IsActive)
            .Select(r => new
            {
                r.Availability,
                r.DateTime
            })
            .ToListAsync();

        if (responses.Count != activeUsersCount || responses.Any(r =>
                r.Availability is Availability.No or Availability.Unknown))
            return null;

        if (responses.Any(r => r.DateTime == null || r.DateTime.Value.TimeOfDay == TimeSpan.Zero))
            return null;

        var commonTime = responses
            .Max(r => ConvertToMoscow(r.DateTime!.Value));

        return commonTime;
    }

    async Task SavePlannedGame(DateTime dateTime, Message message)
    {
        var now = DateTime.UtcNow;
        var dateTimeUtc = ConvertToUtc(dateTime);

        await db.SavedGame.Where(sg => sg.DateTime <= now.Date).ExecuteDeleteAsync();

        if (!await db.SavedGame.AnyAsync(sg => sg.DateTime.Date == dateTimeUtc.Date))
        {
            await db.AddAsync(new SavedGame
            {
                DateTime = dateTimeUtc
            });
        }

        await db.SaveChangesAsync();

        var timeUntilGame = dateTimeUtc - DateTime.UtcNow;
        var activePlayerTags = await db.Users
            .Where(u => u.IsActive)
            .Select(u => $"@{u.Username}")
            .ToListAsync();

        foreach (var timeSpan in ReminderIntervals.Where(i => i <= timeUntilGame))
        {
            var executionTime = dateTimeUtc.Add(-timeSpan);

            var text = $"""
                        {string.Join(", ", activePlayerTags)}

                        АХТУНГ! Игра через {timeSpan.Humanize(culture: _russianCultureInfo, toWords: true)}
                        """;

            var schedulingResult = await ticker.AddAsync(new TimeTickerEntity
            {
                Function = "send_reminder",
                ExecutionTime = executionTime,
                Request = TickerHelper.CreateTickerRequest(new SendReminderJobContext
                {
                    Message = text,
                    ChatId = message.Chat.Id,
                    ThreadId = message.MessageThreadId
                }),
            });

            if (schedulingResult.IsSucceeded)
            {
                LogReminderScheduledTo(logger, executionTime);
            }
        }

        var sb = new StringBuilder();

        foreach (var game in db.SavedGame)
        {
            var gameDateTime = ConvertToMoscow(game.DateTime);
            var dateStr = gameDateTime.ToString("dd.MM.yyyy (ddd) HH:mm", _russianCultureInfo);
            sb.AppendLine($"- {dateStr}");
        }

        await bot.SendMessage(message.Chat.Id,
            messageThreadId: message.MessageThreadId,
            text: $"""
                   Игра сохранена! Запланированные игры:

                   {sb}
                   """,
            parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new ReplyKeyboardRemove());
    }
}
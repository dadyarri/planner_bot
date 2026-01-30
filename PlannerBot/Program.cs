using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PlannerBot;
using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using User = PlannerBot.Data.User;

using var cts = new CancellationTokenSource();
var token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ??
            throw new Exception("TELEGRAM_TOKEN environment variable not set");
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ??
                  throw new Exception("DATABASE_URL environment variable not set");
var databaseOptions = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(databaseUrl);

await using (var db = new AppDbContext(databaseOptions.Options))
{
    await db.Database.MigrateAsync();
}

var bot = new TelegramBotClient(token, cancellationToken: cts.Token);
var me = await bot.GetMe();

bot.OnMessage += OnMessage;
bot.OnUpdate += OnUpdate;
while (true) ;

async Task OnMessage(Message message, UpdateType update)
{
    if (message.Text is not { } text)
    {
        Console.WriteLine($"Received a message of type {message.Type}");
    }
    else if (text.StartsWith('/'))
    {
        var space = text.IndexOf(' ');
        if (space < 0) space = text.Length;
        var command = text[..space].ToLower();
        if (command.LastIndexOf('@') is > 0 and var at) // it's a targeted command
            if (command[(at + 1)..].Equals(me.Username, StringComparison.OrdinalIgnoreCase))
                command = command[..at];
            else
                return; // command was not targeted at me
        await OnCommand(command, text[space..].TrimStart(), message);
    }
}

async Task OnUpdate(Update update)
{
    if (update.Type == UpdateType.CallbackQuery)
    {
        var split = update.CallbackQuery!.Data!.Split(";");
        switch (split[0])
        {
            case "plan":
            {
                var availability = int.Parse(split[1]);
                var data = new PlanButtonCallback
                {
                    Availability = (Availability)availability,
                    Date = DateTime.ParseExact(split[2], "dd/MM/yyyy", CultureInfo.InvariantCulture),
                    Username = split[3],
                };
                var newAvailability = (Availability)((int)(data.Availability + 1) % 4);

                if (data.Username != update.CallbackQuery.From.Username)
                {
                    await bot.AnswerCallbackQuery(update.CallbackQuery!.Id, "Не твоя кнопка!");
                    return;
                }

                var date = DateOnly.FromDateTime(data.Date);
                await UpdateResponseForDate(update.CallbackQuery.From, newAvailability, date);

                if (newAvailability == Availability.Yes)
                {
                    await bot.DeleteMessage(update.CallbackQuery.Message!.Chat.Id, update.CallbackQuery.Message.Id);
                    await bot.SendMessage(update.CallbackQuery.Message!.Chat.Id,
                        messageThreadId: update.CallbackQuery.Message!.MessageThreadId,
                        text: "Выбери время, начиная с которого ты свободен",
                        replyMarkup: await GenerateTimeKeyboard(date, data.Username));
                }
                else
                {
                    await bot.EditMessageReplyMarkup(update.CallbackQuery.Message!.Chat.Id,
                        update.CallbackQuery.Message.Id,
                        await GeneratePlanKeyboard(update.CallbackQuery.Message, data.Username));
                }

                break;
            }
            case "ptime":
            {
                await using var db = new AppDbContext(databaseOptions.Options);
                var date = DateOnly.ParseExact(split[1], "dd/MM/yyyy", CultureInfo.InvariantCulture);
                var time = TimeOnly.ParseExact(split[2], "HH:mm", CultureInfo.InvariantCulture);
                var username = split[3];
                
                if (username != update.CallbackQuery.From.Username)
                {
                    await bot.AnswerCallbackQuery(update.CallbackQuery!.Id, "Не твоя кнопка!");
                    return;
                }

                var response = await db.Responses
                    .Include(r => r.User)
                    .Where(r => r.User.Username == username && r.Date == date)
                    .FirstOrDefaultAsync();

                response?.Time = time;
                await db.SaveChangesAsync();

                await bot.DeleteMessage(update.CallbackQuery.Message!.Chat.Id, update.CallbackQuery.Message.Id);
                await bot.SendMessage(update.CallbackQuery.Message!.Chat.Id,
                    messageThreadId: update.CallbackQuery.Message!.MessageThreadId,
                    text: "Здесь можно настроить свободные дни в ближайшее время:",
                    replyMarkup: await GeneratePlanKeyboard(update.CallbackQuery.Message!, username));

                break;
            }
            case "pback":
            {
                var username = split[1];
                await bot.EditMessageReplyMarkup(update.CallbackQuery.Message!.Chat.Id, update.CallbackQuery.Message.Id,
                    await GeneratePlanKeyboard(update.CallbackQuery.Message!, username));
                break;
            }
            case "save":
            {
                var date = DateOnly.ParseExact(split[1], "dd/MM/yyyy", CultureInfo.InvariantCulture);
                var time = TimeOnly.ParseExact(split[2], "HH:mm", CultureInfo.InvariantCulture);

                await SavePlannedGame(date, time, update.CallbackQuery.Message!);

                break;
            }
            case "delete":
            {
                await bot.DeleteMessage(update.CallbackQuery.Message!.Chat.Id, update.CallbackQuery.Message.Id);
                await bot.DeleteMessage(update.CallbackQuery.Message!.Chat.Id, update.CallbackQuery.Message.Id - 1);
                break;
            }
        }

        await bot.AnswerCallbackQuery(update.CallbackQuery!.Id);
    }
}

async Task OnCommand(string command, string args, Message msg)
{
    Console.WriteLine($"Received command: {command} {args}");
    switch (command)
    {
        case "/start":
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
            break;
        case "/yes":
        {
            if (string.IsNullOrEmpty(args))
            {
                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: "Укажи время, начиная с которого ты свободен (любое, кроме 00:00)",
                    parseMode: ParseMode.Html, linkPreviewOptions: true,
                    replyMarkup: new ReplyKeyboardRemove());
            }

            var suitableTime =
                await UpdateResponseForDate(msg.From!, Availability.Yes, DateOnly.FromDateTime(DateTime.UtcNow), args);
            await bot.SetMessageReaction(msg.Chat, msg.Id, ["❤"]);

            if (suitableTime is not null)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: $"Ура! Сегодня все могут! Удобное время: <b>{suitableTime:HH:mm}</b>",
                    parseMode: ParseMode.Html, linkPreviewOptions: true,
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("Сохранить",
                            $"save;{today:dd/MM/yyyy};{suitableTime:HH:mm}")
                    )
                );
            }


            break;
        }
        case "/no":
        {
            await UpdateResponseForDate(msg.From!, Availability.No, DateOnly.FromDateTime(DateTime.UtcNow));
            await bot.SetMessageReaction(msg.Chat, msg.Id, ["💩"]);

            break;
        }
        case "/prob":
        {
            await UpdateResponseForDate(msg.From!, Availability.Probably, DateOnly.FromDateTime(DateTime.UtcNow));
            await bot.SetMessageReaction(msg.Chat, msg.Id, ["😐"]);

            break;
        }
        case "/get":
        {
            await using var db = new AppDbContext(databaseOptions.Options);

            var users = await db.Users
                .Where(u => u.IsActive)
                .ToListAsync();

            var usernames = users.Select(u => u.Username).ToList();

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var end = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(6));

            var sb = new StringBuilder();

            for (var i = 0; i < 7; i++)
            {
                var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(i));
                sb.AppendLine($"<b>{date.ToString("dd MMM (ddd)", new CultureInfo("ru-RU"))}</b>");
                sb.AppendLine();

                foreach (var user in users)
                {
                    var response = (await db.Responses
                        .Where(r => r.Date == date && r.User.Username == user.Username)
                        .FirstOrDefaultAsync());

                    var time = string.Empty;

                    if (response is not null && response.Availability == Availability.Yes &&
                        response.Date != default)
                    {
                        time = $" (с {response.Time:HH:mm})";
                    }

                    sb.AppendLine(
                        $"{user.Name}: <i>{(response?.Availability ?? Availability.Unknown).ToSign()}{time}</i>");
                }

                sb.AppendLine();
            }

            var nearestFittingDate = await db.Responses
                .Include(v => v.User)
                .Where(v => v.Date >= today &&
                            v.Date <= end &&
                            usernames.Contains(v.User.Username) && v.User.IsActive)
                .GroupBy(v => v.Date)
                .Where(g =>
                    g.Count() == usernames.Count && // все пользователи проголосовали
                    g.All(v => v.Availability != Availability.No)) // нет отказов
                .OrderBy(g => g.Key)
                .Select(g => g.Key)
                .FirstOrDefaultAsync();

            var availableTime = await CheckIfDateIsAvailable(nearestFittingDate);

            var format = nearestFittingDate != default
                ? $"{nearestFittingDate.ToString("dd MMM (ddd)", new CultureInfo("ru-RU"))}{availableTime?.ToString(" HH:mm") ?? string.Empty}"
                : "не найдено";

            sb.Append($"<b>Ближайшая удобная дата</b>: {format}");

            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId, text: sb.ToString(),
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());

            break;
        }
        case "/pause":
        {
            await using var db = new AppDbContext(databaseOptions.Options);

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

            break;
        }
        case "/unpause":
        {
            await using var db = new AppDbContext(databaseOptions.Options);

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

            break;
        }
        case "/plan":
        {
            await using var db = new AppDbContext(databaseOptions.Options);

            var calendar = await GeneratePlanKeyboard(msg);

            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "Здесь можно настроить свободные дни в ближайшее время:", parseMode: ParseMode.Html,
                linkPreviewOptions: true,
                replyMarkup: calendar);

            break;
        }
        case "/set":
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

            await bot.SetMessageReaction(msg.Chat, msg.Id, ["🔥"]);
            await SavePlannedGame(DateOnly.FromDateTime(date), TimeOnly.FromDateTime(date), msg);

            break;
        }
        case "/test":
        {
            var clock = await GenerateTimeKeyboard(DateOnly.FromDateTime(DateTime.Now));
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "Здесь можно настроить свободные дни в ближайшее время:", parseMode: ParseMode.Html,
                linkPreviewOptions: true,
                replyMarkup: clock);
            break;
        }
    }
}

async Task<InlineKeyboardButton[][]> GeneratePlanKeyboard(Message message,
    string? username = null)
{
    await using var db = new AppDbContext(databaseOptions.Options);

    var today = DateTime.Today;

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
                            r.Date == DateOnly.FromDateTime(date))
                .Select(r => r.Availability)
                .FirstOrDefaultAsync();

            var emoji = availability switch
            {
                Availability.Yes => "✅ ",
                Availability.No => "❌ ",
                Availability.Probably => "❓ ",
                _ => string.Empty
            };

            var format = date.ToString("dd.MM (ddd)", new CultureInfo("ru-RU"));
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

async Task<TimeOnly?> UpdateResponseForDate(Telegram.Bot.Types.User from, Availability availability, DateOnly date,
    string? args = null)
{
    var time = TimeOnly.MinValue;
    if (args is not null)
    {
        TimeOnly.TryParseExact(args, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);
    }

    await using var db = new AppDbContext(databaseOptions.Options);

    var response = await db.Responses.Where(r =>
            r.User.Username == from.Username && r.Date == date)
        .FirstOrDefaultAsync();

    if (response is not null)
    {
        response.Availability = availability;
        response.Time = time == TimeOnly.MinValue ? null : time;
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
            Date = date,
            Time = time == TimeOnly.MinValue ? null : time,
            User = user
        };
        await db.Responses.AddAsync(response);
    }

    await db.SaveChangesAsync();

    var suitableTime = await CheckIfDateIsAvailable(date);
    return suitableTime;
}

async Task<InlineKeyboardButton[][]> GenerateTimeKeyboard(
    DateOnly date,
    string? username = null)
{
    await using var db = new AppDbContext(databaseOptions.Options);

    var start = new TimeOnly(9, 0);
    var end = new TimeOnly(20, 30);
    var step = TimeSpan.FromMinutes(30);

    const int slotsPerRow = 4;

    var buttons = new List<InlineKeyboardButton[]>();
    var currentRow = new List<InlineKeyboardButton>();

    for (var time = start; time <= end; time = time.Add(step))
    {
        currentRow.Add(
            InlineKeyboardButton.WithCallbackData(
                $"{time:HH:mm}",
                $"ptime;{date:dd/MM/yyyy};{time:HH:mm};{username}"
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

async Task<TimeOnly?> CheckIfDateIsAvailable(DateOnly date)
{
    await using var db = new AppDbContext(databaseOptions.Options);

    var activeUsersCount = await db.Users
        .Where(u => u.IsActive)
        .CountAsync();

    var responses = await db.Responses
        .Where(r =>
            r.Date == date &&
            r.User.IsActive)
        .Select(r => new
        {
            r.Availability,
            r.Time
        })
        .ToListAsync();

    if (responses.Count != activeUsersCount || responses.Any(r =>
            r.Availability is Availability.No or Availability.Unknown))
        return null;

    if (responses.Any(r => r.Time == null))
        return null;

    var commonTime = responses
        .Max(r => r.Time!.Value);

    return commonTime;
}

async Task SavePlannedGame(DateOnly date, TimeOnly time, Message message)
{
    var now = DateTime.Now;

    await using var db = new AppDbContext(databaseOptions.Options);
    await db.SavedGame.Where(sg => sg.Date <= DateOnly.FromDateTime(now)).ExecuteDeleteAsync();

    await db.AddAsync(new SavedGame
    {
        Date = date,
        Time = time
    });

    await db.SaveChangesAsync();

    var sb = new StringBuilder();

    foreach (var game in db.SavedGame)
    {
        var dateStr = game.Date.ToString("dd.MM.yyyy (ddd)", new CultureInfo("ru-RU"));
        var timeStr = game.Time.ToString("HH:mm", new CultureInfo("ru-RU"));
        sb.AppendLine($"- {dateStr} {timeStr}");
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
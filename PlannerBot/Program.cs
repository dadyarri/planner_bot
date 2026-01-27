using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
    else
    {
        await OnTextMessage(message);
    }
}

async Task OnTextMessage(Message msg)
{
    Console.WriteLine($"Received text '{msg.Text}' in {msg.Chat}");
    await OnCommand("/start", "", msg);
}

async Task OnCommand(string command, string args, Message msg)
{
    Console.WriteLine($"Received command: {command} {args}");
    switch (command)
    {
        case "/start":
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId, text: """
                    <b><u>Меню бота</u></b>:
                    /yes - Указать, что могу играть сегодня
                    /no - Указать, что не могу играть сегодня
                    /prob - Указать, что возможно могу сегодня
                    /plan - Запланировать на две недели
                    
                    /pause - Приостановить участие в играх
                    /unpause - Восстановить участие в играх
                    
                    /get - Показать общий план и ближайшее пересечение
                    /set dd.mm.yyyy hh:mm - Установить время ближайшей игры
                    """, parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());
            break;
        case "/yes":
        {
            await using var db = new AppDbContext(databaseOptions.Options);

            var response = await db.Responses.Where(r =>
                    r.User.Username == msg.From!.Username && r.Date == DateOnly.FromDateTime(DateTime.UtcNow))
                .FirstOrDefaultAsync();

            if (response is not null)
            {
                response.Availability = Availability.Yes;
            }
            else
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
                
                response = new Response
                {
                    Availability = Availability.Yes,
                    Date = DateOnly.FromDateTime(DateTime.UtcNow),
                    User = user
                };
                await db.Responses.AddAsync(response);
            }

            await db.SaveChangesAsync();
            await bot.SetMessageReaction(msg.Chat, msg.Id, ["❤"]);

            break;
        }
        case "/no":
        {
            await using var db = new AppDbContext(databaseOptions.Options);

            var response = await db.Responses
                .Include(u => u.User)
                .Where(r =>
                    r.User.Username == msg.From!.Username && r.Date == DateOnly.FromDateTime(DateTime.UtcNow))
                .FirstOrDefaultAsync();

            if (response is not null)
            {
                response.Availability = Availability.No;
            }
            else
            {
                var user = await db.Users
                    .Where(u => u.Username == msg.From!.Username)
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

                response = new Response
                {
                    Availability = Availability.No,
                    Date = DateOnly.FromDateTime(DateTime.UtcNow),
                    User = user
                };
                await db.Responses.AddAsync(response);
            }

            await db.SaveChangesAsync();
            await bot.SetMessageReaction(msg.Chat, msg.Id, ["💩"]);

            break;
        }
        case "/prob":
        {
            await using var db = new AppDbContext(databaseOptions.Options);

            var response = await db.Responses.Where(r =>
                    r.User.Username == msg.From!.Username && r.Date == DateOnly.FromDateTime(DateTime.UtcNow))
                .FirstOrDefaultAsync();

            if (response is not null)
            {
                response.Availability = Availability.Probably;
            }
            else
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
                
                response = new Response
                {
                    Availability = Availability.Probably,
                    Date = DateOnly.FromDateTime(DateTime.UtcNow),
                    User = user
                };
                await db.Responses.AddAsync(response);
            }

            await db.SaveChangesAsync();
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
            var end = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(13));

            var sb = new StringBuilder();

            for (var i = 0; i < 7; i++)
            {
                var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(i));
                sb.AppendLine($"<b>{date.ToString("dd MMM (ddd)", new CultureInfo("ru-RU"))}</b>");
                sb.AppendLine();

                foreach (var user in users)
                {
                    var availability = (await db.Responses
                        .Where(r => r.Date == date && r.User.Username == user.Username)
                        .FirstOrDefaultAsync())?.Availability ?? Availability.Unknown;

                    sb.AppendLine($"{user.Name}: <i>{availability.ToSign()}</i>");
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

            var format = nearestFittingDate != default
                ? nearestFittingDate.ToString("dd MMM (ddd)", new CultureInfo("ru-RU"))
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
    }
}
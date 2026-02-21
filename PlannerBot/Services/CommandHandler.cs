using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Background;
using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using User = PlannerBot.Data.User;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;

namespace PlannerBot.Services;

/// <summary>
/// Handles execution of bot commands (/start, /yes, /no, /plan, /save, etc.).
/// Each command handler method processes user input and sends appropriate responses.
/// </summary>
public class CommandHandler(
    ITelegramBotClient bot,
    AppDbContext db,
    KeyboardGenerator keyboardGenerator,
    AvailabilityManager availabilityManager,
    TimeZoneUtilities timeZoneUtilities,
    ITimeTickerManager<TimeTickerEntity> ticker,
    ILogger<UpdateHandler> logger)
{
    /// <summary>
    /// Executes a command based on the command string.
    /// </summary>
    public async Task ExecuteCommand(string command, string args, Message msg)
    {
        switch (command)
        {
            case "/start":
                await SendUsage(msg);
                break;
            case "/yes":
                await HandleYesCommand(msg, args);
                break;
            case "/no":
                await HandleNoCommand(msg);
                break;
            case "/prob":
                await HandleProbablyCommand(msg);
                break;
            case "/get":
                await HandleGetCommand(msg);
                break;
            case "/pause":
                await HandlePauseCommand(msg);
                break;
            case "/unpause":
                await HandleUnpauseCommand(msg);
                break;
            case "/plan":
                await HandlePlanCommand(msg);
                break;
            case "/save":
                await HandleSaveCommand(msg, args);
                break;
            case "/saved":
                await HandleSavedCommand(msg);
                break;
            case "/unsave":
                await HandleUnsaveCommand(msg, args);
                break;
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
                /save dd.mm.yyyy hh:mm - Установить время ближайшей игры
                /saved - Показать список сохранённых игр
                /unsave number - Отменить сохранённую игру
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
            await availabilityManager.UpdateResponseForDate(msg.From!, Availability.Yes, 
                timeZoneUtilities.GetMoscowDate(), args);
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["❤"]);

        if (suitableTime is not null)
        {
            var today = timeZoneUtilities.GetMoscowDate().Add(suitableTime.Value.TimeOfDay);
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
        await availabilityManager.UpdateResponseForDate(msg.From!, Availability.No, 
            timeZoneUtilities.GetMoscowDate());

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
        await availabilityManager.UpdateResponseForDate(msg.From!, Availability.Probably, 
            timeZoneUtilities.GetMoscowDate());
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["😐"]);
    }

    private async Task HandleGetCommand(Message msg)
    {
        var users = await db.Users
            .Where(u => u.IsActive)
            .ToListAsync();

        var usernames = users.Select(u => u.Username).ToList();

        var moscowNow = timeZoneUtilities.GetMoscowDateTime();
        var startMoscowDate = moscowNow.Date;
        var endMoscowDate = startMoscowDate.AddDays(6);

        var startUtcDate = timeZoneUtilities.ConvertToUtc(DateTime.SpecifyKind(startMoscowDate, DateTimeKind.Unspecified));
        var endUtcDate = timeZoneUtilities.ConvertToUtc(DateTime.SpecifyKind(endMoscowDate.AddDays(1), DateTimeKind.Unspecified));

        var sb = new StringBuilder();
        var culture = timeZoneUtilities.GetRussianCultureInfo();

        var allResponses = await db.Responses
            .Where(r => r.DateTime.HasValue && r.User.IsActive &&
                        r.DateTime.Value >= startUtcDate && r.DateTime.Value < endUtcDate)
            .ToListAsync();

        for (var i = 0; i < 7; i++)
        {
            var moscowDate = startMoscowDate.AddDays(i);

            sb.AppendLine($"<b>{moscowDate.ToString("dd MMM (ddd)", culture)}</b>");
            sb.AppendLine();

            foreach (var user in users)
            {
                var response = allResponses
                    .FirstOrDefault(r => r.User.Username == user.Username &&
                                         timeZoneUtilities.ConvertToMoscow(r.DateTime!.Value).Date == moscowDate);

                var time = string.Empty;

                if (response is { Availability: Availability.Yes, DateTime: not null } &&
                    response.DateTime.Value.TimeOfDay != TimeSpan.Zero)
                {
                    var moscowTime = timeZoneUtilities.ConvertToMoscow(response.DateTime.Value);
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
                        v.DateTime.Value >= startUtcDate &&
                        v.DateTime.Value < endUtcDate &&
                        usernames.Contains(v.User.Username) && v.User.IsActive)
            .GroupBy(v => v.DateTime!.Value.Date)
            .Where(g =>
                g.Count() == usernames.Count &&
                g.All(v => v.Availability != Availability.No))
            .OrderBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefaultAsync();

        var availableTime = await availabilityManager.CheckIfDateIsAvailable(nearestFittingDate);

        var formattedDate = nearestFittingDate != default
            ? timeZoneUtilities.ConvertToMoscow(nearestFittingDate).ToString("dd MMM (ddd)", culture)
            : string.Empty;
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
        var calendar = await keyboardGenerator.GeneratePlanKeyboard(msg.From!.Username);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "Здесь можно настроить свободные дни в ближайшее время:", parseMode: ParseMode.Html,
            linkPreviewOptions: true,
            replyMarkup: new InlineKeyboardMarkup(calendar));
    }

    private async Task HandleSaveCommand(Message msg, string args)
    {
        if (args == string.Empty)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: """
                      Пропущены аргументы с датой/временем.

                      Пример использования:
                      /save 28.01.2026 18:30
                      """, parseMode: ParseMode.Html,
                linkPreviewOptions: true);
            return;
        }

        if (!DateTime.TryParseExact(args, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: """
                      Невалидный формат даты/времени.

                      Пример использования:
                      /save 28.01.2026 18:30
                      """, parseMode: ParseMode.Html,
                linkPreviewOptions: true);
            return;
        }

        await availabilityManager.SavePlannedGame(date, msg, logger);
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["🔥"]);
    }

    private async Task HandleSavedCommand(Message msg)
    {
        var savedGames = await db.SavedGame
            .Where(sg => sg.DateTime >= DateTime.UtcNow)
            .OrderBy(sg => sg.DateTime)
            .ToListAsync();

        var sb = new StringBuilder("Сохранённые игры:");
        sb.AppendLine();
        sb.AppendLine();

        var culture = timeZoneUtilities.GetRussianCultureInfo();
        foreach (var game in savedGames)
        {
            var gameDateTime = timeZoneUtilities.ConvertToMoscow(game.DateTime);
            sb.AppendLine($"- [{game.Id}] {gameDateTime.ToString("dd.MM.yyyy (ddd) HH:mm", culture)}");
        }

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: sb.ToString(), parseMode: ParseMode.Html,
            linkPreviewOptions: true);
    }

    private async Task HandleUnsaveCommand(Message msg, string args)
    {
        if (!int.TryParse(args, out var id))
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: """
                      Не указан номер игры или указано не число.

                      Пример использования:
                      /unsave 0
                      """
            );
            return;
        }

        var deletedCount = await db.SavedGame.Where(sg => sg.Id == id).ExecuteDeleteAsync();
        
        if (deletedCount == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: $"Игра с номером {id} не найдена"
            );
            return;
        }

        var jobIds = (await db.Set<TimeTickerEntity>()
                .ToListAsync())
            .Where(t => TickerHelper.ReadTickerRequest<SendReminderJobContext>(t.Request).SavedGameId == id)
            .Select(t => t.Id).ToList();

        await ticker.DeleteBatchAsync(jobIds);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "Удалена игра и все связанные с ней напоминания"
        );
    }
}

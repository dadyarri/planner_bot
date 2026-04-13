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
/// Handles execution of bot commands (/start, /yes, /no, /plan, /vote, etc.).
/// Each command handler method processes user input and sends appropriate responses.
/// </summary>
public class CommandHandler(
    ITelegramBotClient bot,
    AppDbContext db,
    KeyboardGenerator keyboardGenerator,
    AvailabilityManager availabilityManager,
    VotingManager votingManager,
    TimeZoneUtilities timeZoneUtilities,
    CampaignManager campaignManager,
    ITimeTickerManager<TimeTickerEntity> ticker,
    ICronTickerManager<CronTickerEntity> cronTicker,
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
            case "/vote":
                await HandleVoteCommand(msg, args);
                break;
            case "/saved":
                await HandleSavedCommand(msg);
                break;
            case "/unsave":
                await HandleUnsaveCommand(msg, args);
                break;
            case "/weekly":
                await HandleWeeklyCommand(msg);
                break;
            case "/campaign_new":
                await HandleCampaignNewCommand(msg);
                break;
            case "/campaign_join":
                await HandleCampaignJoinCommand(msg);
                break;
            case "/campaign_leave":
                await HandleCampaignLeaveCommand(msg);
                break;
            case "/campaign_delete":
                await HandleCampaignDeleteCommand(msg);
                break;
            case "/service_thread":
                await HandleServiceThreadCommand(msg);
                break;
            case "/steal":
                await HandleStealCommand(msg);
                break;
        }
    }

    private async Task SendUsage(Message msg)
    {
        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId, text: """
                <b><u>⚔️ ГРИМУАР ЗАКЛИНАНИЙ ⚔️</u></b>:

                /yes hh:mm - Клянусь участвовать в битве сегодня (укажи час)
                /no - Боги повелели мне остаться в таверне сегодня
                /prob - Судьба туманна, я затрудняюсь с ответом
                /plan - Предсказать свободные дни на неделю вперёд

                /pause - Удалиться на время в монастырь
                /unpause - Вернуться из отшельничества

                /get - Узреть расписание братства и грядущий поход
                /vote dd.mm.yyyy hh:mm - Начать голосование за запись битвы
                /saved - Развернуть свиток начертанных битв
                /unsave number - Стереть запись о битве

                <b>🏰 Управление кампаниями:</b>
                /campaign_new - Основать новую кампанию в этом потоке
                /campaign_join - Вступить в ряды кампании
                /campaign_leave - Покинуть ряды кампании
                /campaign_delete - Завершить кампанию (только Мастер)
                /service_thread - Пометить поток как служебный
                /steal - Захватить свободный слот для битвы (только Мастер)
                """, parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new ReplyKeyboardRemove());
    }

    private async Task HandleYesCommand(Message msg, string? args = null)
    {
        if (string.IsNullOrEmpty(args))
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "🕐 Назови час присоединения к битве (только не полночь, та hora проклята)",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new ReplyKeyboardRemove());
            return;
        }

        await availabilityManager.UpdateResponseForDate(msg.From!, Availability.Yes,
            timeZoneUtilities.GetMoscowDate(), args);
        await bot.SetMessageReaction(msg.Chat, msg.Id, ["❤"]);
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
                text: "⚰️ Битва отменена - боги нынче переменчивы",
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

        var moscowNow = timeZoneUtilities.GetMoscowDateTime();
        var startMoscowDate = moscowNow.Date;
        var endMoscowDate = startMoscowDate.AddDays(13);

        var startUtcDate = timeZoneUtilities.ConvertToUtc(startMoscowDate);
        var endUtcDate = timeZoneUtilities.ConvertToUtc(endMoscowDate.AddDays(1));

        var sb = new StringBuilder();

        var allResponses = await db.Responses
            .Where(r => r.DateTime.HasValue && r.User.IsActive &&
                        r.DateTime.Value >= startUtcDate && r.DateTime.Value < endUtcDate)
            .ToListAsync();

        sb.AppendLine("<b>📅 РАСПИСАНИЕ БРАТСТВА</b>");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        sb.AppendLine();

        for (var i = 0; i < 12; i++)
        {
            var moscowDate = startMoscowDate.AddDays(i);
            var isToday = moscowDate == startMoscowDate;
            var dayMarker = isToday ? " 📍" : string.Empty;

            sb.AppendLine($"<b>{timeZoneUtilities.FormatDate(moscowDate)}</b>{dayMarker}");

            var dayResponses = new List<string>();
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
                    time = $" <i>с {timeZoneUtilities.FormatTime(moscowTime)}</i>";
                }

                var availability = (response?.Availability ?? Availability.Unknown).ToSign();
                dayResponses.Add($"  {availability} {user.Name}{time}");
            }

            sb.AppendLine(string.Join("\n", dayResponses));
            sb.AppendLine();
        }

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
        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: $"🏛️ {user.Name} решил уйти в монастырь. Пусть же боги будут к нему благосклонны!",
            parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new ReplyKeyboardRemove());
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
        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: $"🎉 Поприветствуем {user.Name}, вернувшегося из отшельничества!",
            parseMode: ParseMode.Html, linkPreviewOptions: true,
            replyMarkup: new ReplyKeyboardRemove());

        await bot.SetMessageReaction(msg.Chat, msg.Id, ["🎉"]);
    }

    private async Task HandlePlanCommand(Message msg)
    {
        var calendar = await keyboardGenerator.GeneratePlanKeyboard(msg.From!.Username);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "🗓️ Начертай свой путь на грядущие луны:", parseMode: ParseMode.Html,
            linkPreviewOptions: true,
            replyMarkup: new InlineKeyboardMarkup(calendar));
    }

    private async Task HandleVoteCommand(Message msg, string args)
    {
        if (args == string.Empty)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: """
                      ⚠️ Ты забыл указать дату и час битвы!

                      Используй заклинание так:
                      /vote 28.01.2026 18:30
                      """, parseMode: ParseMode.Html,
                linkPreviewOptions: true);
            return;
        }

        if (!DateTime.TryParseExact(args, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: """
                      💫 Руны не поддаются прочтению!

                      Используй заклинание так:
                      /vote 28.01.2026 18:30
                      """, parseMode: ParseMode.Html,
                linkPreviewOptions: true);
            return;
        }

        var utcGameDateTime = timeZoneUtilities.ConvertToUtc(date);
        var now = DateTime.UtcNow;

        if (utcGameDateTime < now)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Нельзя назначить голосование за битву в прошлом!",
                parseMode: ParseMode.Html, linkPreviewOptions: true);
            return;
        }

        // Resolve campaign from current thread
        var campaign = await campaignManager.ResolveCampaignFromContext(msg.Chat.Id, msg.MessageThreadId);
        if (campaign is null)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Эту команду можно использовать только в потоке кампании.",
                parseMode: ParseMode.Html, linkPreviewOptions: true);
            return;
        }

        // Build mentions from campaign members
        var memberUserIds = await db.CampaignMembers
            .Where(cm => cm.CampaignId == campaign.Id)
            .Select(cm => cm.UserId)
            .ToListAsync();
        var memberUsers = await db.Users
            .Where(u => memberUserIds.Contains(u.Id))
            .ToListAsync();
        var activeMentions = string.Join(" ", memberUsers.Select(u => $"@{u.Username}"));

        await votingManager.SendVotingMessage(
            msg.Chat.Id, msg.MessageThreadId, utcGameDateTime,
            msg.From!.Username!, activeMentions, keyboardGenerator, campaign.Id);

        await bot.SetMessageReaction(msg.Chat, msg.Id, ["🔥"]);
    }

    private async Task HandleSavedCommand(Message msg)
    {
        var savedGames = await db.SavedGame
            .Where(sg => sg.DateTime >= DateTime.UtcNow)
            .OrderBy(sg => sg.DateTime)
            .ToListAsync();

        var sb = new StringBuilder("📜 Летопись битв грядущих:");
        sb.AppendLine();
        sb.AppendLine();

        foreach (var game in savedGames)
        {
            var gameDateTime = timeZoneUtilities.ConvertToMoscow(game.DateTime);
            sb.AppendLine($"- [{game.Id}] {timeZoneUtilities.FormatDateTime(gameDateTime)}");
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
                      ❌ Номер битвы не найден или написан неправильно.

                      Используй заклинание так:
                      /unsave 0
                      """
            );
            return;
        }

        var deletedCount = await db.SavedGame.Where(sg => sg.Id == id).ExecuteDeleteAsync();

        if (deletedCount == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: $"🔍 В летописях не найдена битва №{id}"
            );
            return;
        }

        var jobIds = (await db.Set<TimeTickerEntity>()
                .ToListAsync())
            .Where(t => TickerHelper.ReadTickerRequest<SendReminderJobContext>(t.Request).SavedGameId == id)
            .Select(t => t.Id).ToList();

        await ticker.DeleteBatchAsync(jobIds);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "🗡️ Битва вычеркнута из летописи"
        );
    }

    private async Task HandleWeeklyCommand(Message msg)
    {
        const string votingReminderFunctionName = "send_weekly_voting_reminder";
        const string votingReminderCron = "0 0 21 * * 6"; // Every Saturday 9pm UTC

        // Check if voting reminder job already exists
        var existingJobs = await db.Set<CronTickerEntity>()
            .Where(c => c.Function == votingReminderFunctionName)
            .ToListAsync();

        if (existingJobs.Count > 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "🔔 Глас уже вещает каждую седмицу!");
            return;
        }

        try
        {
            await cronTicker.AddAsync(new CronTickerEntity
            {
                Function = votingReminderFunctionName,
                Expression = votingReminderCron,
                Request = TickerHelper.CreateTickerRequest(new WeeklyVotingReminderJobContext
                {
                    ChatId = msg.Chat.Id,
                    ThreadId = msg.MessageThreadId
                })
            });

            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "✅ Глас будет вещать каждый день Сатурна в час ужина!");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule weekly voting reminder");
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "❌ Чёрная магия помешала - глас не смог явиться на зов");
        }
    }

    private async Task HandleCampaignNewCommand(Message msg)
    {
        if (msg.MessageThreadId is null)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Эту команду можно использовать только в потоке форума.",
                parseMode: ParseMode.Html);
            return;
        }

        var user = await EnsureUser(msg);
        var (campaign, error) = await campaignManager.CreateCampaign(
            msg.Chat.Id, msg.MessageThreadId.Value, user.Id);

        if (error is not null)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: error, parseMode: ParseMode.Html);
            return;
        }

        // Auto-join the DM as a member
        await campaignManager.JoinCampaign(campaign!.Id, user.Id);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: $"🏰 Кампания основана! {user.Name} встаёт за ширму Мастера Подземелий.",
            parseMode: ParseMode.Html);
    }

    private async Task HandleCampaignJoinCommand(Message msg)
    {
        var user = await EnsureUser(msg);

        // If in a campaign thread, join that campaign directly
        if (msg.MessageThreadId is not null)
        {
            var campaign = await campaignManager.ResolveCampaignFromContext(
                msg.Chat.Id, msg.MessageThreadId);

            if (campaign is not null)
            {
                var error = await campaignManager.JoinCampaign(campaign.Id, user.Id);
                if (error is not null)
                {
                    await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                        text: error, parseMode: ParseMode.Html);
                    return;
                }

                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: $"⚔️ {user.Name} вступает в ряды кампании!",
                    parseMode: ParseMode.Html);
                return;
            }
        }

        // Service thread or unknown thread — show campaign picker
        var campaigns = await campaignManager.GetActiveCampaigns(msg.Chat.Id);
        if (campaigns.Count == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ В этом чате нет активных кампаний.",
                parseMode: ParseMode.Html);
            return;
        }

        var keyboard = keyboardGenerator.GenerateCampaignPickerKeyboard(
            CallbackActions.CampaignJoin, campaigns, msg.From!.Username!);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "⚔️ Выбери кампанию, в которую хочешь вступить:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    private async Task HandleCampaignLeaveCommand(Message msg)
    {
        var user = await EnsureUser(msg);

        // If in a campaign thread, leave that campaign directly
        if (msg.MessageThreadId is not null)
        {
            var campaign = await campaignManager.ResolveCampaignFromContext(
                msg.Chat.Id, msg.MessageThreadId);

            if (campaign is not null)
            {
                var error = await campaignManager.LeaveCampaign(campaign.Id, user.Id);
                if (error is not null)
                {
                    await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                        text: error, parseMode: ParseMode.Html);
                    return;
                }

                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: $"👋 {user.Name} покидает ряды кампании.",
                    parseMode: ParseMode.Html);
                return;
            }
        }

        // Service thread or unknown thread — show campaign picker (user's campaigns)
        var campaigns = await campaignManager.GetUserCampaigns(user.Id);
        if (campaigns.Count == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Ты не состоишь ни в одной кампании.",
                parseMode: ParseMode.Html);
            return;
        }

        var keyboard = keyboardGenerator.GenerateCampaignPickerKeyboard(
            CallbackActions.CampaignLeave, campaigns, msg.From!.Username!);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "👋 Выбери кампанию, которую хочешь покинуть:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    private async Task HandleCampaignDeleteCommand(Message msg)
    {
        var user = await EnsureUser(msg);

        // If in a campaign thread, delete that campaign directly (DM-only)
        if (msg.MessageThreadId is not null)
        {
            var campaign = await campaignManager.ResolveCampaignFromContext(
                msg.Chat.Id, msg.MessageThreadId);

            if (campaign is not null)
            {
                var error = await campaignManager.DeleteCampaign(campaign.Id, user.Id);
                if (error is not null)
                {
                    await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                        text: error, parseMode: ParseMode.Html);
                    return;
                }

                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: "📕 Кампания завершена — летопись запечатана.",
                    parseMode: ParseMode.Html);
                return;
            }
        }

        // Service thread — show DM's campaigns
        var campaigns = await campaignManager.GetDmCampaigns(user.Id);
        if (campaigns.Count == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ У тебя нет активных кампаний для завершения.",
                parseMode: ParseMode.Html);
            return;
        }

        var keyboard = keyboardGenerator.GenerateCampaignPickerKeyboard(
            CallbackActions.CampaignDelete, campaigns, msg.From!.Username!);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "📕 Выбери кампанию для завершения:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    private async Task HandleServiceThreadCommand(Message msg)
    {
        if (msg.MessageThreadId is null)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Эту команду можно использовать только в потоке форума.",
                parseMode: ParseMode.Html);
            return;
        }

        var (isServiceThread, error) = await campaignManager.ToggleServiceThread(
            msg.Chat.Id, msg.MessageThreadId.Value);

        if (error is not null)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: error, parseMode: ParseMode.Html);
            return;
        }

        var text = isServiceThread
            ? "📜 Этот поток отныне служебный — канцелярия Мастеров."
            : "⚔️ Служебная метка снята — поток свободен для кампаний.";

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: text, parseMode: ParseMode.Html);
    }

    private async Task HandleStealCommand(Message msg)
    {
        var user = await EnsureUser(msg);

        // In a campaign thread — show slots for that campaign (DM-only)
        if (msg.MessageThreadId is not null)
        {
            var campaign = await campaignManager.ResolveCampaignFromContext(
                msg.Chat.Id, msg.MessageThreadId);

            if (campaign is not null)
            {
                if (campaign.DungeonMasterId != user.Id)
                {
                    await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                        text: "⚠️ Только Мастер Подземелий может призвать /steal!",
                        parseMode: ParseMode.Html);
                    return;
                }

                await ShowSlotPickerForCampaign(msg.Chat.Id, msg.MessageThreadId, campaign.Id, user.Username);
                return;
            }
        }

        // Service thread or unknown thread — show campaign picker filtered to DM campaigns
        var dmCampaigns = await campaignManager.GetDmCampaigns(user.Id);
        if (dmCampaigns.Count == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ У тебя нет кампаний, которыми ты управляешь.",
                parseMode: ParseMode.Html);
            return;
        }

        var keyboard = keyboardGenerator.GenerateCampaignPickerKeyboard(
            CallbackActions.StealCampaign, dmCampaigns, msg.From!.Username!);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "🎯 Выбери кампанию, для которой хочешь захватить слот:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    /// <summary>
    /// Shows the slot picker for a campaign, filtering out past slots.
    /// Shared between the campaign thread flow and the service thread callback.
    /// </summary>
    internal async Task ShowSlotPickerForCampaign(long chatId, int? threadId, int campaignId, string username)
    {
        var now = DateTime.UtcNow;
        var slots = await db.AvailableSlots
            .Where(s => s.CampaignId == campaignId && s.DateTime > now)
            .OrderBy(s => s.DateTime)
            .ToListAsync();

        if (slots.Count == 0)
        {
            await bot.SendMessage(chatId, messageThreadId: threadId,
                text: "⚠️ Свободных слотов не найдено. Попроси игроков заполнить /plan.",
                parseMode: ParseMode.Html);
            return;
        }

        var keyboard = keyboardGenerator.GenerateSlotPickerKeyboard(campaignId, slots, username);

        await bot.SendMessage(chatId, messageThreadId: threadId,
            text: "🎲 Выбери свободный слот для битвы:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    /// <summary>
    /// Ensures the user exists in the database. Creates if not found.
    /// </summary>
    private async Task<User> EnsureUser(Message msg)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Username == msg.From!.Username);

        if (user is not null)
            return user;

        user = new User
        {
            Username = msg.From!.Username ?? throw new UnreachableException(),
            Name = $"{msg.From!.FirstName} {msg.From!.LastName}".Trim(),
            IsActive = true
        };
        await db.Users.AddAsync(user);
        await db.SaveChangesAsync();

        return user;
    }
}
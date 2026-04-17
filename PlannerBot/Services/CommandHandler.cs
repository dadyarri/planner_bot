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
    CampaignOrderService campaignOrderService,
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
            case "/campaign_next":
                await HandleCampaignNextCommand(msg);
                break;
            case "/service_thread":
                await HandleServiceThreadCommand(msg);
                break;
            case "/order":
                await HandleOrderCommand(msg);
                break;
            case "/order_set":
                await HandleOrderSetCommand(msg);
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
                /vote [dd.mm.yyyy hh:mm] - Захватить свободный слот или назначить конкретный час битвы (только Мастер)
                /saved - Развернуть свиток начертанных битв
                /unsave - Стереть запись о битве

                <b>🏰 Управление кампаниями:</b>
                /campaign_new - Основать новую кампанию в этом потоке
                /campaign_join - Вступить в ряды кампании
                /campaign_leave - Покинуть ряды кампании
                /campaign_next - Передать ход следующей кампании (только текущий Мастер)
                /service_thread - Пометить поток как служебный

                <b>🗓️ Очерёдность кампаний:</b>
                /order - Посмотреть очерёдность походов
                /order_set - Настроить очерёдность кампаний
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
        var user = await EnsureUser(msg);
        var calendar = await keyboardGenerator.GeneratePlanKeyboard(user.Id);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "🗓️ Начертай свой путь на грядущие луны:", parseMode: ParseMode.Html,
            linkPreviewOptions: true,
            replyMarkup: new InlineKeyboardMarkup(calendar));
    }

    private async Task HandleVoteCommand(Message msg, string args)
    {
        var user = await EnsureUser(msg);
        var campaign = await campaignManager.ResolveCampaignFromContext(msg.Chat.Id, msg.MessageThreadId);

        // No args → slot picker mode (former /steal behaviour)
        if (args == string.Empty)
        {
            if (msg.MessageThreadId is not null)
            {
                if (campaign is not null)
                {
                    if (campaign.DungeonMasterId != user.Id)
                    {
                        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                            text: "⚠️ Только Мастер Подземелий может начать голосование!",
                            parseMode: ParseMode.Html, linkPreviewOptions: true);
                        return;
                    }

                    // Soft turn-order enforcement
                    var currentHolder = await campaignOrderService.GetCurrentCampaign(msg.Chat.Id);
                    if (currentHolder is not null && currentHolder.Id != campaign.Id)
                    {
                        var keyboard = keyboardGenerator.GenerateOrderOverrideKeyboard(campaign.Id, string.Empty, user.Id);
                        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                            text: $"⚠️ Сейчас очередь кампании <b>{currentHolder.ForumThread.Name}</b>. Продолжить голосование вне очереди?",
                            parseMode: ParseMode.Html, linkPreviewOptions: true,
                            replyMarkup: new InlineKeyboardMarkup(keyboard));
                        return;
                    }

                    await ShowSlotPickerForCampaign(msg.Chat.Id, msg.MessageThreadId, campaign.Id, user.Id);
                    return;
                }
            }

            // Service thread or unknown — show DM campaign picker
            var dmCampaigns = await campaignManager.GetDmCampaigns(user.Id);
            if (dmCampaigns.Count == 0)
            {
                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: "⚠️ У тебя нет кампаний, которыми ты управляешь. Используй /campaign_new в потоке форума.",
                    parseMode: ParseMode.Html, linkPreviewOptions: true);
                return;
            }

            var slotPickerKeyboard = keyboardGenerator.GenerateCampaignPickerKeyboard(
                CallbackActions.VotePickCampaign, dmCampaigns, user.Id);
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "🎯 Выбери кампанию, для которой хочешь захватить слот:",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(slotPickerKeyboard));
            return;
        }

        // Args provided → manual date/time mode
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
        if (campaign is null)
        {
            // Not in a campaign thread — show DM campaign picker with the target timestamp embedded
            var dmCampaigns = await campaignManager.GetDmCampaigns(user.Id);
            if (dmCampaigns.Count == 0)
            {
                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: "⚠️ У тебя нет кампаний, которыми ты управляешь. Используй /campaign_new в потоке форума.",
                    parseMode: ParseMode.Html, linkPreviewOptions: true);
                return;
            }

            var pickerKeyboard = keyboardGenerator.GenerateVoteCampaignPickerKeyboard(
                dmCampaigns, utcGameDateTime, user.Id);
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: $"🗡️ Выбери кампанию для голосования за {timeZoneUtilities.FormatDateTime(timeZoneUtilities.ConvertToMoscow(utcGameDateTime))}:",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(pickerKeyboard));
            return;
        }

        // DM-only
        if (campaign.DungeonMasterId != user.Id)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Только Мастер Подземелий может начать голосование в этой кампании!",
                parseMode: ParseMode.Html, linkPreviewOptions: true);
            return;
        }

        // Soft turn-order enforcement
        var turnHolder = await campaignOrderService.GetCurrentCampaign(msg.Chat.Id);
        if (turnHolder is not null && turnHolder.Id != campaign.Id)
        {
            var overrideKeyboard = keyboardGenerator.GenerateOrderOverrideKeyboard(
                campaign.Id, utcGameDateTime.ToString("yyMMddHHmm"), user.Id);
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: $"⚠️ Сейчас очередь кампании <b>{turnHolder.ForumThread.Name}</b>. Продолжить голосование вне очереди?",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(overrideKeyboard));
            return;
        }

        // Build mentions from active campaign members
        var memberUserIds = await db.CampaignMembers
            .Where(cm => cm.CampaignId == campaign.Id)
            .Select(cm => cm.UserId)
            .ToListAsync();
        var memberUsers = await db.Users
            .Where(u => memberUserIds.Contains(u.Id) && u.IsActive)
            .ToListAsync();

        // Collision detection
        var conflictingCampaigns = await GetConflictingCampaignNames(
            campaign.Id, utcGameDateTime, memberUsers.Select(u => u.Id).ToList());

        if (conflictingCampaigns.Count > 0)
        {
            var conflictList = string.Join("\n", conflictingCampaigns.Select(n => $"  — {n}"));
            var keyboard = keyboardGenerator.GenerateVoteCollisionKeyboard(campaign.Id, utcGameDateTime, user.Id);
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: $"""
                       ⚠️ Внимание, Мастер! В этот день уже записаны битвы в других кампаниях:

                       {conflictList}

                       Некоторые воины могут быть заняты. Продолжить голосование?
                       """,
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(keyboard));
            return;
        }

        var activeMentions = string.Join(" ", memberUsers.Select(u => $"@{u.Username}"));
        await votingManager.SendVotingMessage(
            campaign.ForumThread.ChatId, campaign.ForumThread.ThreadId, utcGameDateTime,
            user.Id, activeMentions, keyboardGenerator, campaign.Id);

        await bot.SetMessageReaction(msg.Chat, msg.Id, ["🔥"]);
    }

    private async Task HandleSavedCommand(Message msg)
    {
        var campaign = await campaignManager.ResolveCampaignFromContext(msg.Chat.Id, msg.MessageThreadId);
        if (campaign is null)
        {
            // Not in a campaign thread — show all campaigns picker
            var user = await EnsureUser(msg);
            var allCampaigns = await campaignManager.GetActiveCampaigns(msg.Chat.Id);
            if (allCampaigns.Count == 0)
            {
                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: "⚠️ В этом чате нет активных кампаний.",
                    parseMode: ParseMode.Html, linkPreviewOptions: true);
                return;
            }

            var pickerKeyboard = keyboardGenerator.GenerateCampaignPickerKeyboard(
                CallbackActions.SavedCampaignPick, allCampaigns, user.Id);
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "📜 Выбери кампанию, чтобы узреть её летопись:",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(pickerKeyboard));
            return;
        }

        await SendSavedGamesForCampaign(msg.Chat.Id, msg.MessageThreadId, campaign.Id);
    }

    /// <summary>
    /// Sends a list of upcoming saved games for the given campaign.
    /// Shared between the direct command flow and the SavedCampaignPick callback.
    /// </summary>
    internal async Task SendSavedGamesForCampaign(long chatId, int? threadId, int campaignId)
    {
        var savedGames = await db.SavedGame
            .Where(sg => sg.DateTime >= DateTime.UtcNow && sg.CampaignId == campaignId)
            .OrderBy(sg => sg.DateTime)
            .ToListAsync();

        var sb = new StringBuilder("📜 Летопись битв грядущих:");
        sb.AppendLine();
        sb.AppendLine();

        if (savedGames.Count == 0)
        {
            sb.AppendLine("— ни одной битвы не запланировано.");
        }
        else
        {
            foreach (var game in savedGames)
            {
                var gameDateTime = timeZoneUtilities.ConvertToMoscow(game.DateTime);
                sb.AppendLine($"- {timeZoneUtilities.FormatDateTime(gameDateTime)}");
            }
        }

        await bot.SendMessage(chatId, messageThreadId: threadId,
            text: sb.ToString(), parseMode: ParseMode.Html,
            linkPreviewOptions: true);
    }

    private async Task HandleUnsaveCommand(Message msg, string args)
    {
        var user = await EnsureUser(msg);

        var campaign = await campaignManager.ResolveCampaignFromContext(msg.Chat.Id, msg.MessageThreadId);
        if (campaign is null)
        {
            // Not in a campaign thread — show DM campaigns picker
            var dmCampaigns = await campaignManager.GetDmCampaigns(user.Id);
            if (dmCampaigns.Count == 0)
            {
                await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                    text: "⚠️ У тебя нет кампаний, которыми ты управляешь.",
                    parseMode: ParseMode.Html, linkPreviewOptions: true);
                return;
            }

            var pickerKeyboard = keyboardGenerator.GenerateCampaignPickerKeyboard(
                CallbackActions.UnsaveCampaignPick, dmCampaigns, user.Id);
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "🗡️ Выбери кампанию, из летописи которой хочешь удалить битву:",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(pickerKeyboard));
            return;
        }

        if (campaign.DungeonMasterId != user.Id)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Только Мастер Подземелий может стереть запись о битве!",
                parseMode: ParseMode.Html, linkPreviewOptions: true);
            return;
        }

        await ShowUnsaveKeyboardForCampaign(msg.Chat.Id, msg.MessageThreadId, campaign.Id, user.Id,
            editMessageId: null);
    }

    /// <summary>
    /// Sends or edits a message showing the unsave inline keyboard for a campaign.
    /// Pass <paramref name="editMessageId"/> to edit an existing message instead of sending a new one.
    /// </summary>
    internal async Task ShowUnsaveKeyboardForCampaign(
        long chatId, int? threadId, int campaignId, long userId, int? editMessageId)
    {
        var now = DateTime.UtcNow;
        var futureGames = await db.SavedGame
            .Where(sg => sg.DateTime > now && sg.CampaignId == campaignId)
            .OrderBy(sg => sg.DateTime)
            .ToListAsync();

        if (futureGames.Count == 0)
        {
            var noGamesText = "📜 Нет грядущих битв для отмены.";
            if (editMessageId.HasValue)
                await bot.EditMessageText(chatId, editMessageId.Value, noGamesText, parseMode: ParseMode.Html);
            else
                await bot.SendMessage(chatId, messageThreadId: threadId, text: noGamesText,
                    parseMode: ParseMode.Html, linkPreviewOptions: true);
            return;
        }

        var keyboard = keyboardGenerator.GenerateUnsaveKeyboard(futureGames, userId);
        if (editMessageId.HasValue)
        {
            await bot.EditMessageText(chatId, editMessageId.Value,
                "🗡️ Выбери битву, которую хочешь вычеркнуть из летописи:",
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(keyboard));
        }
        else
        {
            await bot.SendMessage(chatId, messageThreadId: threadId,
                text: "🗡️ Выбери битву, которую хочешь вычеркнуть из летописи:",
                parseMode: ParseMode.Html, linkPreviewOptions: true,
                replyMarkup: new InlineKeyboardMarkup(keyboard));
        }
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
            CallbackActions.CampaignJoin, campaigns, user.Id);

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
            CallbackActions.CampaignLeave, campaigns, user.Id);

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "👋 Выбери кампанию, которую хочешь покинуть:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    private async Task HandleCampaignNextCommand(Message msg)
    {
        var user = await EnsureUser(msg);
        var chatId = msg.Chat.Id;

        // Resolve which campaign currently holds the turn
        var currentCampaign = await campaignOrderService.GetCurrentCampaign(chatId);

        if (currentCampaign is null)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Очерёдность походов не настроена. Используй /order_set.",
                parseMode: ParseMode.Html);
            return;
        }

        // Only the DM of the current turn-holder may advance the turn
        if (currentCampaign.DungeonMasterId != user.Id)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ Передать ход может только Мастер текущей кампании в очереди.",
                parseMode: ParseMode.Html);
            return;
        }

        // Peek at the next campaign to show in the confirmation message
        var ordered = await campaignOrderService.GetOrderedCampaigns(chatId);
        var currentIndex = ordered.FindIndex(c => c.Id == currentCampaign.Id);
        Campaign? nextCampaign = null;
        if (ordered.Count > 0)
        {
            nextCampaign = currentIndex < 0 || currentIndex == ordered.Count - 1
                ? ordered[0]
                : ordered[currentIndex + 1];
        }

        var nextName = nextCampaign?.ForumThread.Name ?? "?";

        var keyboard = keyboardGenerator.GenerateCampaignNextConfirmKeyboard(user.Id);
        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: $"⚔️ Передать ход кампании <b>{currentCampaign.ForumThread.Name}</b>? Следующей в очереди станет <b>{nextName}</b>.",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    private async Task HandleOrderCommand(Message msg)
    {
        var chatId = msg.Chat.Id;
        var ordered = await campaignOrderService.GetOrderedCampaigns(chatId);

        if (ordered.Count == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "📜 Очерёдность походов не настроена. Используй /order_set.",
                parseMode: ParseMode.Html);
            return;
        }

        var currentCampaign = await campaignOrderService.GetCurrentCampaign(chatId);

        var sb = new StringBuilder("📜 <b>Очерёдность походов:</b>\n\n");
        for (var i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            var isCurrent = currentCampaign?.Id == c.Id;
            var prefix = isCurrent ? "🎯" : "  ";
            var dmLabel = string.IsNullOrWhiteSpace(c.DungeonMaster.Username)
                ? c.DungeonMaster.Name
                : $"@{c.DungeonMaster.Username}";
            sb.AppendLine($"{prefix} {i + 1}. {c.ForumThread.Name} (Мастер: {dmLabel})");
        }

        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: sb.ToString(), parseMode: ParseMode.Html);
    }

    private async Task HandleOrderSetCommand(Message msg)
    {
        var user = await EnsureUser(msg);
        var chatId = msg.Chat.Id;

        var allCampaigns = await campaignManager.GetActiveCampaigns(chatId);
        if (allCampaigns.Count == 0)
        {
            await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
                text: "⚠️ В этом чате нет активных кампаний.",
                parseMode: ParseMode.Html);
            return;
        }

        // Initialise draft from the current saved order
        var ordered = await campaignOrderService.GetOrderedCampaigns(chatId);
        var initialDraft = ordered.Select(c => c.Id).ToList();
        await campaignOrderService.SaveDraft(user.Id, chatId, initialDraft);

        var keyboard = keyboardGenerator.GenerateOrderSetKeyboard(allCampaigns, initialDraft, user.Id);
        await bot.SendMessage(msg.Chat, messageThreadId: msg.MessageThreadId,
            text: "⚙️ Настрой очерёдность кампаний. Нажимай на кампании, чтобы выстроить порядок:",
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


    /// <summary>
    /// Shows the slot picker for a campaign, filtering out past slots.
    /// Shared between the campaign thread flow and the service thread callback.
    /// </summary>
    internal async Task ShowSlotPickerForCampaign(long chatId, int? threadId, int campaignId, long userId)
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

        var keyboard = keyboardGenerator.GenerateSlotPickerKeyboard(campaignId, slots, userId);

        await bot.SendMessage(chatId, messageThreadId: threadId,
            text: "🎲 Выбери свободный слот для битвы:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard));
    }

    /// <summary>
    /// Finds campaign names (other than <paramref name="campaignId"/>) that have a saved game
    /// on the same date as <paramref name="utcGameDateTime"/> and share at least one active member.
    /// Used for collision detection before creating a vote.
    /// </summary>
    internal async Task<List<string>> GetConflictingCampaignNames(
        int campaignId, DateTime utcGameDateTime, List<long> activeMemberIds)
    {
        var dateUtc = utcGameDateTime.Date;
        return await db.SavedGame
            .Where(sg => sg.CampaignId != campaignId &&
                         sg.DateTime.Date == dateUtc &&
                         db.CampaignMembers.Any(cm =>
                             activeMemberIds.Contains(cm.UserId) && cm.CampaignId == sg.CampaignId))
            .Select(sg => sg.Campaign.ForumThread.Name)
            .Distinct()
            .ToListAsync();
    }

    /// <summary>
    /// Deletes a saved game and cancels all associated TickerQ reminder jobs.
    /// Used by both the /unsave command and the UnsaveGame callback.
    /// </summary>
    internal async Task DeleteSavedGameAndReminders(int savedGameId)
    {
        await db.SavedGame.Where(sg => sg.Id == savedGameId).ExecuteDeleteAsync();

        var jobIds = (await db.Set<TimeTickerEntity>().ToListAsync())
            .Where(t => TickerHelper.ReadTickerRequest<SendReminderJobContext>(t.Request).SavedGameId == savedGameId)
            .Select(t => t.Id)
            .ToList();

        await ticker.DeleteBatchAsync(jobIds);
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
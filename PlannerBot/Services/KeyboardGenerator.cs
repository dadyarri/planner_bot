using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types.ReplyMarkups;
using PlannerBot.Data;

namespace PlannerBot.Services;

/// <summary>
/// Generates inline keyboards for Telegram bot interactions.
/// Responsible for creating calendars and time selection menus.
/// All callback payloads use DB user IDs (not usernames) and short action prefixes
/// to stay within Telegram's 64-byte callback data limit.
/// </summary>
public class KeyboardGenerator(AppDbContext db, TimeZoneUtilities timeZoneUtilities)
{
    /// <summary>
    /// Generates a keyboard for planning availability over 12 days.
    /// Displays current availability status for each day.
    /// </summary>
    public async Task<InlineKeyboardButton[][]> GeneratePlanKeyboard(long userId, int? commandMessageId = null)
    {
        var now = DateTime.UtcNow;
        var today = now.Date;

        const int weeks = 3;
        const int daysInWeek = 4;
        const int totalDays = weeks * daysInWeek;

        var endDate = today.AddDays(totalDays);

        // Fetch all availability data for the date range in a single query
        var availabilityByDate = await db.Responses
            .Where(r => r.User.Id == userId &&
                        r.DateTime.HasValue &&
                        r.DateTime.Value.Date >= today &&
                        r.DateTime.Value.Date < endDate)
            .Select(r => new { Date = r.DateTime!.Value.Date, r.Availability })
            .ToDictionaryAsync(r => r.Date, r => r.Availability);

        var culture = timeZoneUtilities.GetRussianCultureInfo();
        var inlineKeyboardButtons = new InlineKeyboardButton[weeks + 1][];

        for (var w = 0; w < weeks; w++)
        {
            inlineKeyboardButtons[w] = new InlineKeyboardButton[daysInWeek];
            for (var d = 0; d < daysInWeek; d++)
            {
                var offset = w * daysInWeek + d;
                var date = today.AddDays(offset);

                availabilityByDate.TryGetValue(date, out var availability);

                var emoji = availability switch
                {
                    Availability.Yes => "🟢 ",
                    Availability.No => "🔴 ",
                    Availability.Probably => "🤷 ",
                    _ => string.Empty
                };

                var format = date.ToString("dd.MM (ddd)", culture);
                inlineKeyboardButtons[w][d] = InlineKeyboardButton.WithCallbackData(
                    $"{emoji}{format}",
                    commandMessageId.HasValue
                        ? $"{CallbackActions.Plan};{date:dd/MM/yyyy};{userId};{commandMessageId.Value}"
                        : $"{CallbackActions.Plan};{date:dd/MM/yyyy};{userId}"
                );
            }
        }

        inlineKeyboardButtons[3] =
        [
            InlineKeyboardButton.WithCallbackData(
                "Закончить",
                commandMessageId.HasValue
                    ? $"{CallbackActions.PlanDone};{commandMessageId.Value}"
                    : CallbackActions.PlanDone
            )
        ];

        return inlineKeyboardButtons;
    }

    /// <summary>
    /// Generates a keyboard for selecting availability status (Yes/No/Probably).
    /// </summary>
    public InlineKeyboardButton[][] GenerateStatusKeyboard(
        DateTime date,
        long userId,
        int? commandMessageId = null)
    {
        return
        [
            [
                InlineKeyboardButton.WithCallbackData("🟢", commandMessageId.HasValue
                    ? $"{CallbackActions.PlanStatus};{(int)Availability.Yes};{date:dd/MM/yyyy};{userId};{commandMessageId.Value}"
                    : $"{CallbackActions.PlanStatus};{(int)Availability.Yes};{date:dd/MM/yyyy};{userId}"),
                InlineKeyboardButton.WithCallbackData("🔴", commandMessageId.HasValue
                    ? $"{CallbackActions.PlanStatus};{(int)Availability.No};{date:dd/MM/yyyy};{userId};{commandMessageId.Value}"
                    : $"{CallbackActions.PlanStatus};{(int)Availability.No};{date:dd/MM/yyyy};{userId}"),
                InlineKeyboardButton.WithCallbackData("🤷", commandMessageId.HasValue
                    ? $"{CallbackActions.PlanStatus};{(int)Availability.Probably};{date:dd/MM/yyyy};{userId};{commandMessageId.Value}"
                    : $"{CallbackActions.PlanStatus};{(int)Availability.Probably};{date:dd/MM/yyyy};{userId}"),
            ],
            [
                InlineKeyboardButton.WithCallbackData("Назад", commandMessageId.HasValue
                    ? $"{CallbackActions.PlanBack};{userId};{commandMessageId.Value}"
                    : $"{CallbackActions.PlanBack};{userId}")
            ]
        ];
    }

    /// <summary>
    /// Generates a keyboard for time selection (6:00 to 17:30) with 30-minute intervals.
    /// </summary>
    public InlineKeyboardButton[][] GenerateTimeKeyboard(
        DateTime date,
        long userId,
        int? commandMessageId = null)
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
            var localDt = timeZoneUtilities.ConvertToMoscow(dt);
            currentRow.Add(
                InlineKeyboardButton.WithCallbackData(
                    localDt.ToString("HH:mm"),
                    commandMessageId.HasValue
                        ? $"{CallbackActions.PlanTime};{dt:dd/MM/yyyyTHH:mm};{userId};{commandMessageId.Value}"
                        : $"{CallbackActions.PlanTime};{dt:dd/MM/yyyyTHH:mm};{userId}"
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
                commandMessageId.HasValue
                    ? $"{CallbackActions.PlanBack};{userId};{commandMessageId.Value}"
                    : $"{CallbackActions.PlanBack};{userId}"
            )
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Generates a keyboard with a cancel button for voting sessions.
    /// The cancel button embeds the creator's DB user ID for ownership verification.
    /// </summary>
    public InlineKeyboardButton[][] GenerateVoteCancelKeyboard(long creatorUserId)
    {
        return
        [
            [
                InlineKeyboardButton.WithCallbackData(
                    "❌ Отменить голосование",
                    $"{CallbackActions.VoteCancel};{creatorUserId}")
            ]
        ];
    }

    /// <summary>
    /// Generates a pager keyboard for the /get schedule view.
    /// Shows 3 days per page across the 12-day window and a close button to remove the message.
    /// </summary>
    public InlineKeyboardButton[][] GenerateGetScheduleKeyboard(int page, int totalPages, long userId, int? commandMessageId = null)
    {
        var navigationButtons = new List<InlineKeyboardButton>();

        if (page > 0)
        {
            navigationButtons.Add(
                InlineKeyboardButton.WithCallbackData(
                    "⬅️ Назад",
                    commandMessageId.HasValue
                        ? $"{CallbackActions.GetPage};{page - 1};{userId};{commandMessageId.Value}"
                        : $"{CallbackActions.GetPage};{page - 1};{userId}")
            );
        }

        if (page < totalPages - 1)
        {
            navigationButtons.Add(
                InlineKeyboardButton.WithCallbackData(
                    "Вперёд ➡️",
                    commandMessageId.HasValue
                        ? $"{CallbackActions.GetPage};{page + 1};{userId};{commandMessageId.Value}"
                        : $"{CallbackActions.GetPage};{page + 1};{userId}")
            );
        }

        var buttons = new List<InlineKeyboardButton[]>();
        if (navigationButtons.Count > 0)
            buttons.Add(navigationButtons.ToArray());

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "❌ Закрыть",
                commandMessageId.HasValue
                    ? $"{CallbackActions.Dismiss};{userId};{commandMessageId.Value}"
                    : $"{CallbackActions.Dismiss};{userId}")
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Generates a campaign picker keyboard for /vote in a service thread.
    /// Embeds the target slot UTC datetime (compact format) so the callback can fire the vote directly.
    /// </summary>
    public InlineKeyboardButton[][] GenerateVoteCampaignPickerKeyboard(
        IReadOnlyList<Campaign> campaigns, DateTime slotUtc, long userId)
    {
        var buttons = campaigns
            .Select(c => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"⚔️ {c.ForumThread.Name}",
                    $"{CallbackActions.VoteCampaignPick};{c.Id};{slotUtc:yyMMddHHmm};{userId}")
            })
            .ToList();

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "❌ Отмена",
                $"{CallbackActions.Dismiss};{userId}")
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Generates a campaign picker keyboard for the given action and campaigns.
    /// Each button shows the campaign name (from linked ForumThread) and embeds campaign ID + user ID.
    /// Includes a cancel button at the bottom.
    /// </summary>
    public InlineKeyboardButton[][] GenerateCampaignPickerKeyboard(
        string action, IReadOnlyList<Campaign> campaigns, long userId)
    {
        var buttons = campaigns
            .Select(c => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"⚔️ {c.ForumThread.Name}",
                    $"{action};{c.Id};{userId}")
            })
            .ToList();

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "❌ Отмена",
                $"{CallbackActions.Dismiss};{userId}")
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Generates a keyboard listing future saved games for /unsave (DM-only inline picker).
    /// Each button shows the game date/time and embeds saved game ID and user ID.
    /// Includes a cancel button at the bottom.
    /// </summary>
    public InlineKeyboardButton[][] GenerateUnsaveKeyboard(
        IReadOnlyList<SavedGame> savedGames, long userId)
    {
        var buttons = savedGames
            .Select(sg =>
            {
                var moscow = timeZoneUtilities.ConvertToMoscow(sg.DateTime);
                return new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"🗡️ {timeZoneUtilities.FormatDateTime(moscow)}",
                        $"{CallbackActions.UnsaveGame};{sg.Id};{userId}")
                };
            })
            .ToList();

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "❌ Отмена",
                $"{CallbackActions.Dismiss};{userId}")
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Generates a proceed/abort keyboard shown when a scheduling collision is detected.
    /// Uses compact datetime format (yyMMddHHmm) to stay within 64-byte callback data limit.
    /// </summary>
    public InlineKeyboardButton[][] GenerateVoteCollisionKeyboard(
        int campaignId, DateTime slotUtc, long userId)
    {
        return
        [
            [
                InlineKeyboardButton.WithCallbackData(
                    "⚔️ Продолжить",
                    $"{CallbackActions.VoteConfirm};{campaignId};{slotUtc:yyMMddHHmm};{userId}"),
                InlineKeyboardButton.WithCallbackData(
                    "❌ Отменить",
                    $"{CallbackActions.VoteAbort};{userId}")
            ]
        ];
    }

    /// <summary>
    /// Generates a slot picker keyboard for /vote (no-args mode).
    /// Each button shows the slot date/time in Moscow time and embeds campaign ID, slot datetime, and user ID.
    /// Uses compact datetime format (yyMMddHHmm) to stay within 64-byte callback data limit.
    /// Includes a cancel button at the bottom.
    /// </summary>
    public InlineKeyboardButton[][] GenerateSlotPickerKeyboard(
        int campaignId, IReadOnlyList<AvailableSlot> slots, long userId)
    {
        var buttons = slots
            .Select(s =>
            {
                var moscow = timeZoneUtilities.ConvertToMoscow(s.DateTime);
                return new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"🗓️ {timeZoneUtilities.FormatDateTime(moscow)}",
                        $"{CallbackActions.VotePickSlot};{campaignId};{s.DateTime:yyMMddHHmm};{userId}")
                };
            })
            .ToList();

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "❌ Отмена",
                $"{CallbackActions.Dismiss};{userId}")
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Generates a confirmation keyboard for /campaign_next.
    /// </summary>
    public InlineKeyboardButton[][] GenerateCampaignNextConfirmKeyboard(long userId)
    {
        return
        [
            [
                InlineKeyboardButton.WithCallbackData(
                    "✅ Передать ход",
                    $"{CallbackActions.CampaignNext};{userId}"),
                InlineKeyboardButton.WithCallbackData(
                    "❌ Отмена",
                    $"{CallbackActions.Dismiss};{userId}")
            ]
        ];
    }

    /// <summary>
    /// Generates a warning keyboard shown when a DM tries to /vote out of turn.
    /// <paramref name="slotStr"/> is either an empty string (no-args path → show slot picker after override)
    /// or a compact datetime string (yyMMddHHmm) for the direct-datetime path.
    /// </summary>
    public InlineKeyboardButton[][] GenerateOrderOverrideKeyboard(int campaignId, string slotStr, long userId)
    {
        return
        [
            [
                InlineKeyboardButton.WithCallbackData(
                    "⚔️ Продолжить",
                    $"{CallbackActions.OrderOverride};{campaignId};{slotStr};{userId}"),
                InlineKeyboardButton.WithCallbackData(
                    "❌ Отмена",
                    $"{CallbackActions.OrderCancel};{userId}")
            ]
        ];
    }

    /// <summary>
    /// Generates the /order_set interactive keyboard.
    /// Each campaign button shows the campaign's current position in the draft (1-based) or "—" if not assigned.
    /// The bottom row has Reset, Cancel, and Save buttons.
    /// </summary>
    public InlineKeyboardButton[][] GenerateOrderSetKeyboard(
        IReadOnlyList<Campaign> allCampaigns, List<int> draftOrder, long userId)
    {
        var buttons = allCampaigns
            .Select(c =>
            {
                var pos = draftOrder.IndexOf(c.Id);
                var label = pos >= 0
                    ? $"{pos + 1}. ⚔️ {c.ForumThread.Name}"
                    : $"— ⚔️ {c.ForumThread.Name}";
                return new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        label,
                        $"{CallbackActions.OrderSetToggle};{c.Id};{userId}")
                };
            })
            .ToList();

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "🔄 Сброс",
                $"{CallbackActions.OrderSetReset};{userId}"),
            InlineKeyboardButton.WithCallbackData(
                "❌ Отмена",
                $"{CallbackActions.OrderSetCancel};{userId}"),
            InlineKeyboardButton.WithCallbackData(
                "💾 Сохранить",
                $"{CallbackActions.OrderSetSave};{userId}")
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Generates a DM picker keyboard for the /campaign_new super-admin flow.
    /// Lists all active users and lets the super-admin pick who becomes the DM.
    /// Each button embeds the target user's DB ID and the callback owner's DB ID.
    /// </summary>
    public InlineKeyboardButton[][] GenerateDmPickerKeyboard(
        IReadOnlyList<User> users, long callbackOwnerId)
    {
        var buttons = users
            .Select(u =>
            {
                var label = string.IsNullOrWhiteSpace(u.Username)
                    ? u.Name
                    : $"@{u.Username}";
                return new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        label,
                        $"{CallbackActions.CampaignNewDmPick};{u.Id};{callbackOwnerId}")
                };
            })
            .ToList();

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "❌ Отмена",
                $"{CallbackActions.Dismiss};{callbackOwnerId}")
        ]);

        return buttons.ToArray();
    }
}

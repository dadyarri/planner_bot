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
    public async Task<InlineKeyboardButton[][]> GeneratePlanKeyboard(long userId)
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
                    $"{CallbackActions.Plan};{date:dd/MM/yyyy};{userId}"
                );
            }
        }

        inlineKeyboardButtons[3] =
        [
            InlineKeyboardButton.WithCallbackData(
                "Закончить",
                CallbackActions.PlanDone
            )
        ];

        return inlineKeyboardButtons;
    }

    /// <summary>
    /// Generates a keyboard for selecting availability status (Yes/No/Probably).
    /// </summary>
    public InlineKeyboardButton[][] GenerateStatusKeyboard(
        DateTime date,
        long userId)
    {
        return
        [
            [
                InlineKeyboardButton.WithCallbackData("🟢", $"{CallbackActions.PlanStatus};{(int)Availability.Yes};{date:dd/MM/yyyy};{userId}"),
                InlineKeyboardButton.WithCallbackData("🔴", $"{CallbackActions.PlanStatus};{(int)Availability.No};{date:dd/MM/yyyy};{userId}"),
                InlineKeyboardButton.WithCallbackData("🤷", $"{CallbackActions.PlanStatus};{(int)Availability.Probably};{date:dd/MM/yyyy};{userId}"),
            ],
            [
                InlineKeyboardButton.WithCallbackData("Назад", $"{CallbackActions.PlanBack};{userId}")
            ]
        ];
    }

    /// <summary>
    /// Generates a keyboard for time selection (6:00 to 17:30) with 30-minute intervals.
    /// </summary>
    public InlineKeyboardButton[][] GenerateTimeKeyboard(
        DateTime date,
        long userId)
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
                    $"{CallbackActions.PlanTime};{dt:dd/MM/yyyyTHH:mm};{userId}"
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
                $"{CallbackActions.PlanBack};{userId}"
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
    /// Generates the out-of-turn warning keyboard for when a DM uses /vote out of rotation.
    /// Uses compact datetime format (yyMMddHHmm) for the slot, or "0" as a sentinel when no
    /// specific datetime is known yet (slot-picker mode).
    /// </summary>
    public InlineKeyboardButton[][] GenerateOutOfTurnKeyboard(int campaignId, DateTime slotUtc, string flowType, long userId)
    {
        var slotParam = slotUtc == DateTime.UnixEpoch ? "0" : slotUtc.ToString("yyMMddHHmm");
        return
        [
            [
                InlineKeyboardButton.WithCallbackData(
                    "✅ Продолжить",
                    $"{CallbackActions.OrderOverride};{campaignId};{flowType};{slotParam};{userId}"),
                InlineKeyboardButton.WithCallbackData(
                    "❌ Отмена",
                    $"{CallbackActions.OrderCancel};{userId}")
            ]
        ];
    }

    /// <summary>
    /// Generates the /order_set inline keyboard for configuring campaign turn order.
    /// <paramref name="serialisedState"/> is a comma-separated list of "campaignId:position" pairs (empty string if none assigned).
    /// <paramref name="savedState"/> is the original state when the keyboard was opened (used for the Reset button).
    /// </summary>
    public InlineKeyboardButton[][] GenerateOrderSetKeyboard(
        IReadOnlyList<Campaign> campaigns, string serialisedState, string savedState, long userId)
    {
        var assigned = ParseOrderState(serialisedState);

        var buttons = campaigns
            .Select(c =>
            {
                var posLabel = assigned.TryGetValue(c.Id, out var pos) ? $" [{pos + 1}]" : string.Empty;
                return new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"⚔️ {c.ForumThread.Name}{posLabel}",
                        $"{CallbackActions.OrderSetToggle};{c.Id};{serialisedState};{userId}")
                };
            })
            .ToList();

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData(
                "🔄 Сброс",
                $"{CallbackActions.OrderSetReset};{savedState};{userId}"),
            InlineKeyboardButton.WithCallbackData(
                "❌ Отмена",
                $"{CallbackActions.OrderSetCancel};{userId}"),
            InlineKeyboardButton.WithCallbackData(
                "💾 Сохранить",
                $"{CallbackActions.OrderSetSave};{serialisedState};{userId}")
        ]);

        return buttons.ToArray();
    }

    /// <summary>
    /// Parses a serialised order state string into a dictionary of campaignId → 0-based position.
    /// </summary>
    public static Dictionary<int, int> ParseOrderState(string serialisedState)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrEmpty(serialisedState))
            return result;

        foreach (var pair in serialisedState.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var cId) && int.TryParse(parts[1], out var p))
                result[cId] = p;
        }

        return result;
    }

    /// <summary>
    /// Serialises a dictionary of campaignId → 0-based position into a state string.
    /// </summary>
    public static string SerialiseOrderState(Dictionary<int, int> state)
    {
        return string.Join(",", state.Select(kv => $"{kv.Key}:{kv.Value}"));
    }
}


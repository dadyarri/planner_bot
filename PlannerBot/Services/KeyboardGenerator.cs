using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using Telegram.Bot.Types.ReplyMarkups;

namespace PlannerBot.Services;

/// <summary>
/// Generates inline keyboards for Telegram bot interactions.
/// Responsible for creating calendars and time selection menus.
/// </summary>
public class KeyboardGenerator(AppDbContext db, TimeZoneUtilities timeZoneUtilities)
{
    /// <summary>
    /// Generates a keyboard for planning availability over 8 days.
    /// Displays current availability status (✅/❌/❓) for each day.
    /// </summary>
    public async Task<InlineKeyboardButton[][]> GeneratePlanKeyboard(
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
                    .Where(r => r.User.Username == username &&
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

                var culture = timeZoneUtilities.GetRussianCultureInfo();
                var format = date.ToString("dd.MM (ddd)", culture);
                inlineKeyboardButtons[w][d] = InlineKeyboardButton.WithCallbackData(
                    $"{emoji}{format}",
                    $"plan;{(int)(availability ?? Availability.Unknown)};{date:dd/MM/yyyy};{username}"
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

    /// <summary>
    /// Generates a keyboard for time selection (6:00 to 17:30) with 30-minute intervals.
    /// </summary>
    public InlineKeyboardButton[][] GenerateTimeKeyboard(
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
            var localDt = timeZoneUtilities.ConvertToMoscow(dt);
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
}

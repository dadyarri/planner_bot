using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;

namespace PlannerBot.Services;

/// <summary>
/// Calculates available game slots by checking if all active users
/// are available on a given date with confirmed start times.
/// </summary>
public class SlotCalculator(AppDbContext db, TimeZoneUtilities timeZoneUtilities)
{
    /// <summary>
    /// Checks if all active users are available on a date and returns their common available time.
    /// Returns null if not all users have confirmed or any user declined.
    /// The returned DateTime is in Moscow time.
    /// </summary>
    public async Task<DateTime?> CheckIfDateIsAvailable(DateTime date)
    {
        var activeUsersCount = await db.Users
            .Where(u => u.IsActive)
            .CountAsync();

        var dateOnly = date.Date;
        var gameExistsOnDate = await db.SavedGame
            .Where(sg => sg.DateTime.Date == dateOnly)
            .AnyAsync();

        if (gameExistsOnDate)
            return null;

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
            .Max(r => timeZoneUtilities.ConvertToMoscow(r.DateTime!.Value));

        return commonTime;
    }

    /// <summary>
    /// Computes available slots for every active campaign over the coming 8 days.
    /// A slot is available for a campaign when every campaign member has
    /// <see cref="Availability.Yes"/> with a confirmed start time on that date
    /// and no <see cref="SavedGame"/> already exists on that date for that campaign.
    /// </summary>
    /// <returns>Dictionary mapping campaign to its list of available UTC DateTimes.</returns>
    public async Task<Dictionary<Campaign, List<DateTime>>> GetAvailableSlotsForAllCampaigns()
    {
        var now = DateTime.UtcNow;
        var result = new Dictionary<Campaign, List<DateTime>>();

        var campaigns = await db.Campaigns
            .Include(c => c.ForumThread)
            .Include(c => c.Members)
            .Where(c => c.IsActive)
            .ToListAsync();

        foreach (var campaign in campaigns)
        {
            var memberUserIds = campaign.Members.Select(m => m.UserId).ToHashSet();
            if (memberUserIds.Count == 0)
                continue;

            var slots = new List<DateTime>();

            for (var i = 0; i < 8; i++)
            {
                var date = now.AddDays(i).Date;

                // Check if a saved game already exists on this date for this campaign
                var gameExists = await db.SavedGame
                    .AnyAsync(sg => sg.CampaignId == campaign.Id && sg.DateTime.Date == date);

                if (gameExists)
                    continue;

                // Get responses from all campaign members for this date
                var responses = await db.Responses
                    .Include(r => r.User)
                    .Where(r =>
                        r.DateTime.HasValue &&
                        r.DateTime.Value.Date == date &&
                        memberUserIds.Contains(r.User.Id))
                    .Select(r => new
                    {
                        r.Availability,
                        r.DateTime
                    })
                    .ToListAsync();

                // All members must have responded
                if (responses.Count != memberUserIds.Count)
                    continue;

                // All must be Yes
                if (responses.Any(r => r.Availability is not Availability.Yes))
                    continue;

                // All must have a confirmed time (non-zero TimeOfDay)
                if (responses.Any(r => r.DateTime == null || r.DateTime.Value.TimeOfDay == TimeSpan.Zero))
                    continue;

                // Common time = max (latest) in Moscow time, then convert back to UTC
                var commonMoscowTime = responses
                    .Max(r => timeZoneUtilities.ConvertToMoscow(r.DateTime!.Value));
                var utcSlot = timeZoneUtilities.ConvertToUtc(commonMoscowTime);

                slots.Add(utcSlot);
            }

            if (slots.Count > 0)
            {
                result[campaign] = slots;
            }
        }

        return result;
    }
}

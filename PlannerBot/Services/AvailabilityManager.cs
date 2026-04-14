using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using User = PlannerBot.Data.User;

namespace PlannerBot.Services;

/// <summary>
/// Manages user availability responses.
/// Handles updating availability for dates and checking if all users are available.
/// </summary>
public class AvailabilityManager
{
    private readonly AppDbContext _db;
    private readonly TimeZoneUtilities _timeZoneUtilities;

    public AvailabilityManager(
        AppDbContext db,
        TimeZoneUtilities timeZoneUtilities)
    {
        _db = db;
        _timeZoneUtilities = timeZoneUtilities;
    }

    /// <summary>
    /// Updates user's availability response for a specific date.
    /// </summary>
    public async Task UpdateResponseForDate(
        Telegram.Bot.Types.User from,
        Availability availability,
        DateTime date,
        string? args = null)
    {
        var time = TimeSpan.Zero;
        if (args is not null)
        {
            var culture = _timeZoneUtilities.GetRussianCultureInfo();
            var dt = DateTime.ParseExact(args, "HH:mm", culture);
            time = dt.TimeOfDay;
        }

        var dateTime = date.Add(time);
        var utcDateTime = _timeZoneUtilities.ConvertToUtc(dateTime);

        var response = await _db.Responses.Where(r =>
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
            var user = await _db.Users.Where(u => u.Username == from.Username)
                .FirstOrDefaultAsync();

            if (user is null)
            {
                user = new User
                {
                    Username = from.Username ?? throw new UnreachableException(),
                    Name = $"{from.FirstName} {from.LastName}".Trim(),
                    IsActive = true
                };
                await _db.Users.AddAsync(user);
            }

            response = new Response
            {
                Availability = availability,
                DateTime = utcDateTime,
                User = user
            };
            await _db.Responses.AddAsync(response);
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Sets availability to "No" (unavailable) for all unmarked days in the plan range (8 days).
    /// This is called when the user finishes planning their weekly availability.
    /// </summary>
    public async Task SetUnavailableForUnmarkedDays(string? username)
    {
        var now = DateTime.UtcNow;
        const int planRangeDays = 8;

        for (var i = 0; i < planRangeDays; i++)
        {
            var date = now.AddDays(i).Date;

            var existingResponse = await _db.Responses
                .Include(r => r.User)
                .Where(r => r.User.Username == username &&
                            r.DateTime.HasValue && r.DateTime.Value.Date == date)
                .FirstOrDefaultAsync();

            // Only set to unavailable if user hasn't marked this day
            if (existingResponse is null)
            {
                var user = await _db.Users.Where(u => u.Username == username)
                    .FirstOrDefaultAsync();

                if (user is not null)
                {
                    // Create a response with No availability for this date
                    var response = new Response
                    {
                        Availability = Availability.No,
                        DateTime = date,
                        User = user
                    };
                    await _db.Responses.AddAsync(response);
                }
            }
        }

        await _db.SaveChangesAsync();
    }
}

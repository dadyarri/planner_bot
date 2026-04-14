namespace PlannerBot.Data;

/// <summary>
/// Cached available game slot for a campaign, computed when any player finishes /plan.
/// Old rows for the same campaign are deleted before inserting new ones.
/// </summary>
public class AvailableSlot
{
    public int Id { get; set; }
    public int CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public DateTime DateTime { get; set; }
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}

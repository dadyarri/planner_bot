namespace PlannerBot.Data;

/// <summary>
/// Records a user's membership in a campaign.
/// A user can be a member of multiple campaigns.
/// </summary>
public class CampaignMember
{
    public int CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

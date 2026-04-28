namespace PlannerBot.Data;

/// <summary>
/// Stores a super-admin's in-progress multi-select draft for manually adding users to a campaign.
/// One row per (UserId, ChatId) pair; deleted when the flow is saved or cancelled.
/// <see cref="SelectedUserIds"/> is a comma-separated list of chosen user IDs.
/// </summary>
public class CampaignJoinDraft
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public int CampaignId { get; set; }
    public string SelectedUserIds { get; set; } = string.Empty;
}

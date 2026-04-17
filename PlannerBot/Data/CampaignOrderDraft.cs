namespace PlannerBot.Data;

/// <summary>
/// Stores a user's in-progress campaign order draft for the /order_set flow.
/// One row per (UserId, ChatId) pair; deleted when the user saves or cancels.
/// <see cref="OrderedCampaignIds"/> is a comma-separated list of campaign IDs in desired order.
/// </summary>
public class CampaignOrderDraft
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string OrderedCampaignIds { get; set; } = string.Empty;
}

namespace PlannerBot.Data;

/// <summary>
/// Tracks which campaign currently holds the turn in the round-robin rotation for a chat.
/// One row per chat; <see cref="CurrentCampaignId"/> is null when no rotation is configured.
/// </summary>
public class CampaignOrderState
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public int? CurrentCampaignId { get; set; }
    public Campaign? CurrentCampaign { get; set; }
}

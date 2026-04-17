namespace PlannerBot.Data;

/// <summary>
/// Single-row-per-chat table holding the global round-robin turn pointer.
/// <see cref="CurrentIndex"/> corresponds to <see cref="Campaign.OrderIndex"/> of the campaign whose turn it is.
/// </summary>
public class CampaignOrderState
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public int CurrentIndex { get; set; }
}

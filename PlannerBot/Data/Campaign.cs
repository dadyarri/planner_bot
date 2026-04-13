namespace PlannerBot.Data;

/// <summary>
/// A D&amp;D campaign linked to a forum thread.
/// The campaign name and chat ID are derived from the linked <see cref="ForumThread"/>.
/// One ForumThread can have at most one active Campaign.
/// </summary>
public class Campaign
{
    public int Id { get; set; }
    public long DungeonMasterId { get; set; }
    public User DungeonMaster { get; set; } = null!;
    public int ForumThreadId { get; set; }
    public ForumThread ForumThread { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CampaignMember> Members { get; set; } = [];
}

namespace PlannerBot.Data;

/// <summary>
/// Tracks an individual user's vote in a voting session.
/// Used for deduplication and showing who has/hasn't voted.
/// </summary>
public class VoteSessionVote
{
    public long Id { get; set; }
    public long VoteSessionId { get; set; }
    public VoteSession VoteSession { get; set; } = null!;
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime VotedAt { get; set; } = DateTime.UtcNow;
}

namespace PlannerBot.Data;

/// <summary>
/// Whether the vote is for or against the proposed time slot.
/// </summary>
public enum VoteType
{
    /// <summary>User voted in favor (👍).</summary>
    For,

    /// <summary>User voted against (👎).</summary>
    Against
}

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
    public VoteType Type { get; set; } = VoteType.For;
    public DateTime VotedAt { get; set; } = DateTime.UtcNow;
}

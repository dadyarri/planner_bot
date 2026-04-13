using System.ComponentModel.DataAnnotations;

namespace PlannerBot.Data;

/// <summary>
/// Possible outcomes of a completed voting session.
/// </summary>
public enum VoteOutcome
{
    /// <summary>Voting is still in progress.</summary>
    Pending,

    /// <summary>Voting reached threshold — game saved to schedule.</summary>
    Saved,

    /// <summary>Voting expired without reaching threshold (timeout).</summary>
    Expired,

    /// <summary>Voting was manually canceled by the creator.</summary>
    Canceled,

    /// <summary>Too many against votes — no consensus reached.</summary>
    NoConsensus
}

/// <summary>
/// Tracks an active voting session for saving a game.
/// Stores message ID for updating vote count and game datetime for reference.
/// </summary>
public class VoteSession
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public int ThreadId { get; set; }
    public DateTime GameDateTime { get; set; }
    public int VoteCount { get; set; }
    public int AgainstCount { get; set; }
    public VoteOutcome Outcome { get; set; } = VoteOutcome.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    [MaxLength(32)]
    public string CreatorUsername { get; set; } = string.Empty;
    public List<VoteSessionVote> Votes { get; set; } = [];
}

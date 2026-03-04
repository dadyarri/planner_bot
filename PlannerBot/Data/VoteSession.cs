namespace PlannerBot.Data;

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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

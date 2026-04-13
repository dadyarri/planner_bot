namespace PlannerBot.Data;

/// <summary>
/// Marks a forum thread as a service (administrative) thread,
/// meaning it is not associated with any campaign.
/// The <c>/service_thread</c> command toggles this designation.
/// </summary>
public class ServiceThread
{
    public int Id { get; set; }
    public int ForumThreadId { get; set; }
    public ForumThread ForumThread { get; set; } = null!;
}

namespace PlannerBot.Background;

public class VoteSessionExpiryJobContext
{
    public long VoteSessionId { get; set; }
    public long ChatId { get; set; }
    public int? ThreadId { get; set; }
    public int MessageId { get; set; }
}

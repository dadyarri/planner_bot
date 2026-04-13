namespace PlannerBot.Background;

public class VoteReminderJobContext
{
    public long VoteSessionId { get; set; }
    public long ChatId { get; set; }
    public int? ThreadId { get; set; }
}

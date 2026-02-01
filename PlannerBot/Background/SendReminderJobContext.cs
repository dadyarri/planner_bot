namespace PlannerBot.Background;

public class SendReminderJobContext
{
    public string Message { get; set; }
    public long ChatId { get; set; }
    public int? ThreadId { get; set; }
}
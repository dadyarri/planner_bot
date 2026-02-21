namespace PlannerBot.Background;

public class SendReminderJobContext
{
    public int ReminderIntervalMinutes { get; set; }
    public long ChatId { get; set; }
    public int? ThreadId { get; set; }
    public int SavedGameId { get; set; }
}
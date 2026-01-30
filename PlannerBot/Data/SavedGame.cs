namespace PlannerBot.Data;

public class SavedGame
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
}
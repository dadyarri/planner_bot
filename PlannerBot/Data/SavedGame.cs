namespace PlannerBot.Data;

public class SavedGame
{
    public int Id { get; set; }
    public DateTime DateTime { get; set; }
    public int CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
}
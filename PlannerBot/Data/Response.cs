using System.ComponentModel.DataAnnotations;

namespace PlannerBot.Data;

public class Response
{
    public long Id { get; set; }
    public DateOnly Date { get; set; }
    public required User User { get; set; }
    public required Availability? Availability { get; set; }
}
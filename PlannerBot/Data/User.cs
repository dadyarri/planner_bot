using System.ComponentModel.DataAnnotations;

namespace PlannerBot.Data;

public class User
{
    public long Id { get; set; }
    [MaxLength(32)]
    public string Username { get; set; }
    [MaxLength(50)]
    public string Name { get; set; }
    public bool IsActive { get; set; }
}
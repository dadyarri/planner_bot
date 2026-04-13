using System.ComponentModel.DataAnnotations;

namespace PlannerBot.Data;

/// <summary>
/// Tracks a forum thread in a Telegram supergroup.
/// The Telegram Bot API does not expose a way to list threads,
/// so the bot tracks them reactively via service messages.
/// </summary>
public class ForumThread
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public int ThreadId { get; set; }
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;
    public bool IsClosed { get; set; }
}

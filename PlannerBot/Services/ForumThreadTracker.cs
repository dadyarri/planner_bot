using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;

namespace PlannerBot.Services;

/// <summary>
/// Tracks forum threads by reacting to Telegram service messages.
/// Keeps <see cref="ForumThread"/> rows in sync with Telegram state
/// using upsert operations keyed on (ChatId, ThreadId).
/// </summary>
public class ForumThreadTracker(AppDbContext db, ILogger<ForumThreadTracker> logger)
{
    /// <summary>
    /// Handles a ForumTopicCreated service message.
    /// Inserts a new ForumThread or updates the existing one.
    /// </summary>
    public async Task OnForumTopicCreated(long chatId, int threadId, string name)
    {
        logger.LogInformation("Forum topic created: ChatId={ChatId}, ThreadId={ThreadId}, Name={Name}",
            chatId, threadId, name);

        var thread = await FindThread(chatId, threadId);
        if (thread is not null)
        {
            thread.Name = name;
            thread.IsClosed = false;
        }
        else
        {
            thread = new ForumThread
            {
                ChatId = chatId,
                ThreadId = threadId,
                Name = name,
                IsClosed = false
            };
            await db.ForumThreads.AddAsync(thread);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Handles a ForumTopicEdited service message.
    /// Updates the thread name if it changed.
    /// </summary>
    public async Task OnForumTopicEdited(long chatId, int threadId, string? newName)
    {
        logger.LogInformation("Forum topic edited: ChatId={ChatId}, ThreadId={ThreadId}, NewName={NewName}",
            chatId, threadId, newName);

        var thread = await FindThread(chatId, threadId);
        if (thread is null)
        {
            // Topic was created before the bot started tracking — insert it now
            thread = new ForumThread
            {
                ChatId = chatId,
                ThreadId = threadId,
                Name = newName ?? string.Empty,
                IsClosed = false
            };
            await db.ForumThreads.AddAsync(thread);
        }
        else if (newName is not null)
        {
            thread.Name = newName;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Handles a ForumTopicClosed service message.
    /// </summary>
    public async Task OnForumTopicClosed(long chatId, int threadId)
    {
        logger.LogInformation("Forum topic closed: ChatId={ChatId}, ThreadId={ThreadId}",
            chatId, threadId);

        var thread = await FindOrCreateThread(chatId, threadId);
        thread.IsClosed = true;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Handles a ForumTopicReopened service message.
    /// </summary>
    public async Task OnForumTopicReopened(long chatId, int threadId)
    {
        logger.LogInformation("Forum topic reopened: ChatId={ChatId}, ThreadId={ThreadId}",
            chatId, threadId);

        var thread = await FindOrCreateThread(chatId, threadId);
        thread.IsClosed = false;
        await db.SaveChangesAsync();
    }

    private async Task<ForumThread?> FindThread(long chatId, int threadId)
    {
        return await db.ForumThreads
            .FirstOrDefaultAsync(ft => ft.ChatId == chatId && ft.ThreadId == threadId);
    }

    private async Task<ForumThread> FindOrCreateThread(long chatId, int threadId)
    {
        var thread = await FindThread(chatId, threadId);
        if (thread is not null)
            return thread;

        thread = new ForumThread
        {
            ChatId = chatId,
            ThreadId = threadId,
            Name = string.Empty,
            IsClosed = false
        };
        await db.ForumThreads.AddAsync(thread);
        return thread;
    }
}

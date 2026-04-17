using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;

namespace PlannerBot.Services;

/// <summary>
/// Manages campaign CRUD operations — creation, membership, soft-deletion,
/// and service thread designation.
/// </summary>
public class CampaignManager(AppDbContext db, ILogger<CampaignManager> logger, CampaignOrderService campaignOrderService)
{
    /// <summary>
    /// Creates a new campaign in the specified forum thread.
    /// The sender becomes the DM.
    /// </summary>
    /// <returns>The created campaign, or null if creation failed due to validation.</returns>
    public async Task<(Campaign? Campaign, string? Error)> CreateCampaign(long chatId, int threadId, long userId)
    {
        // Check if thread is a service thread
        var forumThread = await db.ForumThreads
            .FirstOrDefaultAsync(ft => ft.ChatId == chatId && ft.ThreadId == threadId);

        if (forumThread is null)
        {
            return (null, "⚠️ Этот поток ещё не ведом магии — сначала создай или отредактируй тему форума.");
        }

        var isServiceThread = await db.ServiceThreads
            .AnyAsync(st => st.ForumThreadId == forumThread.Id);

        if (isServiceThread)
        {
            return (null, "⚠️ Нельзя основать кампанию в служебном потоке — это земли канцелярии.");
        }

        // Check if thread already has an active campaign
        var existingCampaign = await db.Campaigns
            .FirstOrDefaultAsync(c => c.ForumThreadId == forumThread.Id && c.IsActive);

        if (existingCampaign is not null)
        {
            return (null, "⚠️ В этом потоке уже ведётся активная кампания.");
        }

        var campaign = new Campaign
        {
            DungeonMasterId = userId,
            ForumThreadId = forumThread.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        await db.Campaigns.AddAsync(campaign);
        await db.SaveChangesAsync();

        logger.LogInformation("Campaign created: Id={CampaignId}, ThreadId={ThreadId}, DM={DmId}",
            campaign.Id, threadId, userId);

        return (campaign, null);
    }

    /// <summary>
    /// Adds a user to a campaign as a member.
    /// </summary>
    public async Task<string?> JoinCampaign(int campaignId, long userId)
    {
        var campaign = await db.Campaigns
            .Include(c => c.ForumThread)
            .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);

        if (campaign is null)
        {
            return "⚠️ Кампания не найдена или более не активна.";
        }

        var alreadyMember = await db.CampaignMembers
            .AnyAsync(cm => cm.CampaignId == campaignId && cm.UserId == userId);

        if (alreadyMember)
        {
            return "⚠️ Ты уже состоишь в этой кампании, воин.";
        }

        var member = new CampaignMember
        {
            CampaignId = campaignId,
            UserId = userId,
            JoinedAt = DateTime.UtcNow
        };

        await db.CampaignMembers.AddAsync(member);
        await db.SaveChangesAsync();

        logger.LogInformation("User {UserId} joined campaign {CampaignId}", userId, campaignId);
        return null;
    }

    /// <summary>
    /// Removes a user from a campaign.
    /// </summary>
    public async Task<string?> LeaveCampaign(int campaignId, long userId)
    {
        var member = await db.CampaignMembers
            .FirstOrDefaultAsync(cm => cm.CampaignId == campaignId && cm.UserId == userId);

        if (member is null)
        {
            return "⚠️ Ты не состоишь в этой кампании.";
        }

        db.CampaignMembers.Remove(member);
        await db.SaveChangesAsync();

        logger.LogInformation("User {UserId} left campaign {CampaignId}", userId, campaignId);
        return null;
    }

    /// <summary>
    /// Soft-deletes a campaign (sets IsActive = false). DM-only.
    /// </summary>
    public async Task<string?> DeleteCampaign(int campaignId, long userId)
    {
        var campaign = await db.Campaigns
            .Include(c => c.ForumThread)
            .FirstOrDefaultAsync(c => c.Id == campaignId && c.IsActive);

        if (campaign is null)
        {
            return "⚠️ Кампания не найдена или уже завершена.";
        }

        if (campaign.DungeonMasterId != userId)
        {
            return "⚠️ Только Мастер Подземелий может завершить кампанию!";
        }

        var previousName = campaign.ForumThread.Name;
        var chatId = campaign.ForumThread.ChatId;
        var wasInRotation = campaign.OrderIndex.HasValue;

        campaign.IsActive = false;
        campaign.OrderIndex = null;
        await db.SaveChangesAsync();

        logger.LogInformation("Campaign {CampaignId} deleted by DM {DmId}", campaignId, userId);

        if (wasInRotation)
            await campaignOrderService.AdvanceTurn(chatId, previousName);

        return null;
    }

    /// <summary>
    /// Toggles service thread designation for the given forum thread.
    /// </summary>
    /// <returns>True if the thread is now a service thread, false if it was un-designated.</returns>
    public async Task<(bool IsServiceThread, string? Error)> ToggleServiceThread(long chatId, int threadId)
    {
        var forumThread = await db.ForumThreads
            .FirstOrDefaultAsync(ft => ft.ChatId == chatId && ft.ThreadId == threadId);

        if (forumThread is null)
        {
            return (false, "⚠️ Этот поток ещё не ведом магии — сначала создай или отредактируй тему форума.");
        }

        var existing = await db.ServiceThreads
            .FirstOrDefaultAsync(st => st.ForumThreadId == forumThread.Id);

        if (existing is not null)
        {
            db.ServiceThreads.Remove(existing);
            await db.SaveChangesAsync();
            logger.LogInformation("Service thread removed: ForumThreadId={ForumThreadId}", forumThread.Id);
            return (false, null);
        }

        // Check if thread has an active campaign — can't designate as service thread
        var hasCampaign = await db.Campaigns
            .AnyAsync(c => c.ForumThreadId == forumThread.Id && c.IsActive);

        if (hasCampaign)
        {
            return (false, "⚠️ В этом потоке ведётся активная кампания — нельзя сделать его служебным.");
        }

        var serviceThread = new ServiceThread
        {
            ForumThreadId = forumThread.Id
        };
        await db.ServiceThreads.AddAsync(serviceThread);
        await db.SaveChangesAsync();

        logger.LogInformation("Service thread added: ForumThreadId={ForumThreadId}", forumThread.Id);
        return (true, null);
    }

    /// <summary>
    /// Resolves which campaign is associated with the given chat and thread.
    /// Returns null if the thread is a service thread, unknown, or has no active campaign.
    /// </summary>
    public async Task<Campaign?> ResolveCampaignFromContext(long chatId, int? threadId)
    {
        if (threadId is null)
            return null;

        var forumThread = await db.ForumThreads
            .FirstOrDefaultAsync(ft => ft.ChatId == chatId && ft.ThreadId == threadId.Value);

        if (forumThread is null)
            return null;

        var isServiceThread = await db.ServiceThreads
            .AnyAsync(st => st.ForumThreadId == forumThread.Id);

        if (isServiceThread)
            return null;

        return await db.Campaigns
            .Include(c => c.ForumThread)
            .FirstOrDefaultAsync(c => c.ForumThreadId == forumThread.Id && c.IsActive);
    }

    /// <summary>
    /// Returns whether the given thread is a service thread.
    /// Null threadId (main chat with no thread) and unknown threads (not yet tracked
    /// via forum topic service messages) are treated as service threads by default,
    /// because campaign commands should only work in explicitly tracked campaign threads.
    /// </summary>
    public async Task<bool> IsServiceThread(long chatId, int? threadId)
    {
        if (threadId is null)
            return true; // No thread ID means main chat, treat as service

        var forumThread = await db.ForumThreads
            .FirstOrDefaultAsync(ft => ft.ChatId == chatId && ft.ThreadId == threadId.Value);

        if (forumThread is null)
            return true; // Unknown thread treated as service

        return await db.ServiceThreads
            .AnyAsync(st => st.ForumThreadId == forumThread.Id);
    }

    /// <summary>
    /// Gets all active campaigns the user is a member of or is the DM of.
    /// </summary>
    public async Task<List<Campaign>> GetUserCampaigns(long userId)
    {
        return await db.Campaigns
            .Include(c => c.ForumThread)
            .Where(c => c.IsActive &&
                        (c.DungeonMasterId == userId ||
                         c.Members.Any(m => m.UserId == userId)))
            .ToListAsync();
    }

    /// <summary>
    /// Gets all active campaigns where the user is the DM.
    /// </summary>
    public async Task<List<Campaign>> GetDmCampaigns(long userId)
    {
        return await db.Campaigns
            .Include(c => c.ForumThread)
            .Where(c => c.IsActive && c.DungeonMasterId == userId)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all active campaigns in the chat.
    /// </summary>
    public async Task<List<Campaign>> GetActiveCampaigns(long chatId)
    {
        return await db.Campaigns
            .Include(c => c.ForumThread)
            .Where(c => c.IsActive && c.ForumThread.ChatId == chatId)
            .ToListAsync();
    }
}

using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;

namespace PlannerBot.Services;

/// <summary>
/// Manages the round-robin campaign turn order.
/// Handles rotation state, draft editing for /order_set, and turn advancement.
/// </summary>
public class CampaignOrderService(AppDbContext db)
{
    /// <summary>
    /// Returns all active campaigns in the chat that are assigned to the rotation,
    /// ordered by <see cref="Campaign.OrderIndex"/> ascending.
    /// </summary>
    public async Task<List<Campaign>> GetOrderedCampaigns(long chatId)
    {
        return await db.Campaigns
            .Include(c => c.ForumThread)
            .Include(c => c.DungeonMaster)
            .Where(c => c.IsActive && c.OrderIndex.HasValue && c.ForumThread.ChatId == chatId)
            .OrderBy(c => c.OrderIndex)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the campaign that currently holds the turn, or null if no rotation is configured
    /// or the current campaign is no longer active / in the rotation.
    /// </summary>
    public async Task<Campaign?> GetCurrentCampaign(long chatId)
    {
        var state = await db.CampaignOrderStates
            .FirstOrDefaultAsync(s => s.ChatId == chatId);

        if (state?.CurrentCampaignId is null)
            return null;

        return await db.Campaigns
            .Include(c => c.ForumThread)
            .Include(c => c.DungeonMaster)
            .FirstOrDefaultAsync(c =>
                c.Id == state.CurrentCampaignId &&
                c.IsActive &&
                c.OrderIndex.HasValue);
    }

    /// <summary>
    /// Advances the turn to the next campaign in the ring.
    /// Returns (previous, next) campaigns. Both may be null when the rotation is empty.
    /// </summary>
    public async Task<(Campaign? Previous, Campaign? Next)> AdvanceTurn(long chatId)
    {
        var ordered = await GetOrderedCampaigns(chatId);
        if (ordered.Count == 0)
            return (null, null);

        var state = await db.CampaignOrderStates
            .FirstOrDefaultAsync(s => s.ChatId == chatId);

        Campaign? previous = null;
        Campaign next;

        if (state?.CurrentCampaignId is not null)
        {
            previous = ordered.FirstOrDefault(c => c.Id == state.CurrentCampaignId);
            var currentIndex = ordered.FindIndex(c => c.Id == state.CurrentCampaignId);
            next = currentIndex < 0 || currentIndex == ordered.Count - 1
                ? ordered[0]
                : ordered[currentIndex + 1];
        }
        else
        {
            next = ordered[0];
        }

        if (state is null)
        {
            state = new CampaignOrderState { ChatId = chatId, CurrentCampaignId = next.Id };
            await db.CampaignOrderStates.AddAsync(state);
        }
        else
        {
            state.CurrentCampaignId = next.Id;
        }

        await db.SaveChangesAsync();
        return (previous, next);
    }

    /// <summary>
    /// Assigns <see cref="Campaign.OrderIndex"/> values to campaigns in the given order.
    /// Campaigns not in <paramref name="orderedCampaignIds"/> have their index cleared.
    /// If the current turn-holder is still present in the new order, it keeps the turn.
    /// If it was removed, the pointer resets to the first campaign in the new order.
    /// </summary>
    public async Task SetOrder(long chatId, List<int> orderedCampaignIds)
    {
        var allCampaigns = await db.Campaigns
            .Include(c => c.ForumThread)
            .Where(c => c.IsActive && c.ForumThread.ChatId == chatId)
            .ToListAsync();

        foreach (var c in allCampaigns)
            c.OrderIndex = null;

        for (var i = 0; i < orderedCampaignIds.Count; i++)
        {
            var campaign = allCampaigns.FirstOrDefault(c => c.Id == orderedCampaignIds[i]);
            if (campaign is not null)
                campaign.OrderIndex = i;
        }

        var state = await db.CampaignOrderStates
            .FirstOrDefaultAsync(s => s.ChatId == chatId);

        if (orderedCampaignIds.Count > 0)
        {
            if (state is null)
            {
                state = new CampaignOrderState
                {
                    ChatId = chatId,
                    CurrentCampaignId = orderedCampaignIds[0]
                };
                await db.CampaignOrderStates.AddAsync(state);
            }
            else if (state.CurrentCampaignId is null ||
                     !orderedCampaignIds.Contains(state.CurrentCampaignId.Value))
            {
                // Current turn-holder removed from rotation — reset to first
                state.CurrentCampaignId = orderedCampaignIds[0];
            }
            // If current holder is still in the list, keep the turn as-is
        }
        else if (state is not null)
        {
            state.CurrentCampaignId = null;
        }

        await db.SaveChangesAsync();
    }

    // ── Draft management ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the draft order for the given user and chat.
    /// Returns an empty list if no draft exists.
    /// </summary>
    public async Task<List<int>> GetDraft(long userId, long chatId)
    {
        var draft = await db.CampaignOrderDrafts
            .FirstOrDefaultAsync(d => d.UserId == userId && d.ChatId == chatId);

        if (draft is null || string.IsNullOrEmpty(draft.OrderedCampaignIds))
            return [];

        return draft.OrderedCampaignIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToList();
    }

    /// <summary>
    /// Persists (creates or updates) the draft order for the given user and chat.
    /// </summary>
    public async Task SaveDraft(long userId, long chatId, List<int> orderedIds)
    {
        var draft = await db.CampaignOrderDrafts
            .FirstOrDefaultAsync(d => d.UserId == userId && d.ChatId == chatId);

        if (draft is null)
        {
            draft = new CampaignOrderDraft { UserId = userId, ChatId = chatId };
            await db.CampaignOrderDrafts.AddAsync(draft);
        }

        draft.OrderedCampaignIds = string.Join(",", orderedIds);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Toggles a campaign in/out of the user's draft order.
    /// If the campaign is already in the draft it is removed; otherwise it is appended.
    /// </summary>
    public async Task ToggleDraftCampaign(long userId, long chatId, int campaignId)
    {
        var order = await GetDraft(userId, chatId);

        if (order.Contains(campaignId))
            order.Remove(campaignId);
        else
            order.Add(campaignId);

        await SaveDraft(userId, chatId, order);
    }

    /// <summary>
    /// Deletes the draft for the given user and chat.
    /// </summary>
    public async Task DeleteDraft(long userId, long chatId)
    {
        await db.CampaignOrderDrafts
            .Where(d => d.UserId == userId && d.ChatId == chatId)
            .ExecuteDeleteAsync();
    }
}

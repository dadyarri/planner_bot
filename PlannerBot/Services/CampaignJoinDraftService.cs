using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;

namespace PlannerBot.Services;

/// <summary>
/// Manages the super-admin draft used by /campaign_join for multi-select member additions.
/// </summary>
public class CampaignJoinDraftService(AppDbContext db)
{
    /// <summary>
    /// Returns the draft for the given user and chat, or null if none exists.
    /// </summary>
    public async Task<CampaignJoinDraft?> GetDraft(long userId, long chatId)
    {
        return await db.CampaignJoinDrafts
            .FirstOrDefaultAsync(d => d.UserId == userId && d.ChatId == chatId);
    }

    /// <summary>
    /// Creates or resets the draft for the given user/chat/campaign.
    /// </summary>
    public async Task InitializeDraft(long userId, long chatId, int campaignId)
    {
        var draft = await GetDraft(userId, chatId);
        if (draft is null)
        {
            draft = new CampaignJoinDraft
            {
                UserId = userId,
                ChatId = chatId
            };
            await db.CampaignJoinDrafts.AddAsync(draft);
        }

        draft.CampaignId = campaignId;
        draft.SelectedUserIds = string.Empty;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the selected user IDs for the draft, or an empty list if none exists.
    /// </summary>
    public async Task<List<long>> GetSelectedUserIds(long userId, long chatId)
    {
        var draft = await GetDraft(userId, chatId);
        if (draft is null || string.IsNullOrEmpty(draft.SelectedUserIds))
            return [];

        return draft.SelectedUserIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(long.Parse)
            .ToList();
    }

    /// <summary>
    /// Toggles a user in or out of the current draft selection.
    /// </summary>
    public async Task ToggleSelectedUser(long userId, long chatId, long targetUserId)
    {
        var selectedUserIds = await GetSelectedUserIds(userId, chatId);

        if (selectedUserIds.Contains(targetUserId))
            selectedUserIds.Remove(targetUserId);
        else
            selectedUserIds.Add(targetUserId);

        await SaveSelectedUserIds(userId, chatId, selectedUserIds);
    }

    /// <summary>
    /// Deletes the draft for the given user and chat.
    /// </summary>
    public async Task DeleteDraft(long userId, long chatId)
    {
        await db.CampaignJoinDrafts
            .Where(d => d.UserId == userId && d.ChatId == chatId)
            .ExecuteDeleteAsync();
    }

    private async Task SaveSelectedUserIds(long userId, long chatId, List<long> selectedUserIds)
    {
        var draft = await GetDraft(userId, chatId);
        if (draft is null)
            return;

        draft.SelectedUserIds = string.Join(",", selectedUserIds);
        await db.SaveChangesAsync();
    }
}

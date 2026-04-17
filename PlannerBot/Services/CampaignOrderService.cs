using Microsoft.EntityFrameworkCore;
using PlannerBot.Data;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace PlannerBot.Services;

/// <summary>
/// Manages the round-robin turn queue for campaigns in a chat.
/// </summary>
public class CampaignOrderService(AppDbContext db, ITelegramBotClient bot, ILogger<CampaignOrderService> logger)
{

    /// <summary>
    /// Returns all active campaigns in the chat that participate in the rotation,
    /// ordered by <see cref="Campaign.OrderIndex"/> ascending.
    /// </summary>
    public async Task<List<Campaign>> GetOrderedCampaigns(long chatId)
    {
        return await db.Campaigns
            .Include(c => c.ForumThread)
            .Include(c => c.DungeonMaster)
            .Where(c => c.IsActive && c.ForumThread.ChatId == chatId && c.OrderIndex != null)
            .OrderBy(c => c.OrderIndex)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the campaign whose turn it currently is, or <c>null</c> if no rotation is set up.
    /// Advances the pointer automatically if the current campaign is no longer valid.
    /// </summary>
    public async Task<Campaign?> GetCurrentCampaign(long chatId)
    {
        var state = await db.CampaignOrderStates.FirstOrDefaultAsync(s => s.ChatId == chatId);
        if (state is null)
            return null;

        var campaigns = await GetOrderedCampaigns(chatId);
        if (campaigns.Count == 0)
            return null;

        var current = campaigns.FirstOrDefault(c => c.OrderIndex == state.CurrentIndex);
        if (current is not null)
            return current;

        // Pointer is stale — find the next valid campaign by walking forward from CurrentIndex
        var next = campaigns.FirstOrDefault(c => c.OrderIndex > state.CurrentIndex)
                   ?? campaigns.First();
        state.CurrentIndex = next.OrderIndex!.Value;
        await db.SaveChangesAsync();

        logger.LogWarning("CampaignOrderService: stale pointer in chat {ChatId}, advanced to campaign {CampaignId}",
            chatId, next.Id);
        return next;
    }

    /// <summary>
    /// Advances the turn pointer to the next campaign in the ring and posts announcements.
    /// </summary>
    public async Task AdvanceTurn(long chatId, string? previousCampaignName = null)
    {
        var campaigns = await GetOrderedCampaigns(chatId);
        if (campaigns.Count == 0)
            return;

        var state = await db.CampaignOrderStates.FirstOrDefaultAsync(s => s.ChatId == chatId);
        if (state is null)
            return;

        // Find the next campaign after CurrentIndex (wrapping around)
        var nextCampaign = campaigns.FirstOrDefault(c => c.OrderIndex > state.CurrentIndex)
                           ?? campaigns.First();

        state.CurrentIndex = nextCampaign.OrderIndex!.Value;

        // Ensure the next campaign is marked active. In normal flow it should already be active,
        // but this guards against edge cases where a campaign was deactivated without going through DeleteCampaign.
        nextCampaign.IsActive = true;

        await db.SaveChangesAsync();

        // Post announcement to the next campaign's thread
        var nextDmMention = nextCampaign.DungeonMaster.Username is not null
            ? $"@{nextCampaign.DungeonMaster.Username}"
            : nextCampaign.DungeonMaster.Name;

        await bot.SendMessage(
            nextCampaign.ForumThread.ChatId,
            messageThreadId: nextCampaign.ForumThread.ThreadId,
            text: $"🎲 Теперь ваш ход, Мастер {nextDmMention}! Очередь кампании <b>{nextCampaign.ForumThread.Name}</b> пришла — самое время объявить дату следующей битвы.",
            parseMode: ParseMode.Html);

        // Post announcement to all service threads in the chat
        var serviceThreads = await db.ServiceThreads
            .Include(st => st.ForumThread)
            .Where(st => st.ForumThread.ChatId == chatId)
            .ToListAsync();

        foreach (var serviceThread in serviceThreads)
        {
            var prevName = previousCampaignName ?? "Предыдущая кампания";
            await bot.SendMessage(
                serviceThread.ForumThread.ChatId,
                messageThreadId: serviceThread.ForumThread.ThreadId,
                text: $"⚔️ Кампания <b>{prevName}</b> передала ход. Следующий в очереди — <b>{nextCampaign.ForumThread.Name}</b> (Мастер: {nextDmMention}).",
                parseMode: ParseMode.Html);
        }

        logger.LogInformation("CampaignOrderService: turn advanced to campaign {CampaignId} (OrderIndex={OrderIndex}) in chat {ChatId}",
            nextCampaign.Id, nextCampaign.OrderIndex, chatId);
    }

    /// <summary>
    /// Sets the rotation order for campaigns in the chat.
    /// Campaigns in <paramref name="campaignIds"/> receive sequential <see cref="Campaign.OrderIndex"/> values (0-based).
    /// Campaigns not in the list get <c>null</c>. Resets the pointer to position 0.
    /// </summary>
    public async Task SetOrder(long chatId, IReadOnlyList<int> campaignIds)
    {
        // Clear all OrderIndex values for campaigns in this chat first
        var allCampaigns = await db.Campaigns
            .Include(c => c.ForumThread)
            .Where(c => c.ForumThread.ChatId == chatId && c.IsActive)
            .ToListAsync();

        foreach (var campaign in allCampaigns)
            campaign.OrderIndex = null;

        // Assign new indices
        for (var i = 0; i < campaignIds.Count; i++)
        {
            var campaign = allCampaigns.FirstOrDefault(c => c.Id == campaignIds[i]);
            if (campaign is not null)
                campaign.OrderIndex = i;
        }

        // Upsert the CampaignOrderState row, resetting pointer to 0
        var state = await db.CampaignOrderStates.FirstOrDefaultAsync(s => s.ChatId == chatId);
        if (state is null)
        {
            state = new CampaignOrderState { ChatId = chatId, CurrentIndex = 0 };
            await db.CampaignOrderStates.AddAsync(state);
        }
        else
        {
            state.CurrentIndex = 0;
        }

        await db.SaveChangesAsync();

        logger.LogInformation("CampaignOrderService: order set for chat {ChatId} with {Count} campaigns",
            chatId, campaignIds.Count);
    }
}

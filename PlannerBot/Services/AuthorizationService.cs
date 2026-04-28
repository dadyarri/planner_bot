using PlannerBot.Data;

namespace PlannerBot.Services;

/// <summary>
/// Centralizes bot-level authorization checks shared by command and callback flows.
/// </summary>
public class AuthorizationService
{
    /// <summary>
    /// Returns true if the user is the designated super-admin who may act on behalf of any DM.
    /// </summary>
    public bool IsSuperAdmin(User user) =>
        string.Equals(user.Username, BotConstants.SuperAdminUsername, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the user can perform DM-restricted actions for the campaign.
    /// </summary>
    public bool CanManageCampaign(Campaign campaign, User user) =>
        campaign.DungeonMasterId == user.Id || IsSuperAdmin(user);
}

namespace PlannerBot.Services;

/// <summary>
/// Constants for callback query action prefixes used in inline keyboard buttons.
/// These values are the first segment of semicolon-delimited callback data strings.
/// Values are kept short to stay within Telegram's 64-byte callback data limit.
/// </summary>
public static class CallbackActions
{
    /// <summary>User selected a day in the /plan calendar.</summary>
    public const string Plan = "p";

    /// <summary>User selected an availability status (Yes/No/Probably) for a day.</summary>
    public const string PlanStatus = "ps";

    /// <summary>User selected a start time for a day.</summary>
    public const string PlanTime = "pt";

    /// <summary>User pressed the "Back" button in the planning flow.</summary>
    public const string PlanBack = "pb";

    /// <summary>User pressed the "Done" button to finish planning.</summary>
    public const string PlanDone = "pd";

    /// <summary>Creator pressed the cancel button on a voting session.</summary>
    public const string VoteCancel = "vc";

    /// <summary>User selected a campaign from the campaign picker to join.</summary>
    public const string CampaignJoin = "cj";

    /// <summary>User selected a campaign from the campaign picker to leave.</summary>
    public const string CampaignLeave = "cl";

    /// <summary>DM selected a campaign from the campaign picker to delete.</summary>
    public const string CampaignPause = "cd";

    /// <summary>DM selected a campaign from the campaign picker for /steal (service thread flow).</summary>
    public const string StealCampaign = "sc";

    /// <summary>DM selected an available slot from the slot picker for /steal.</summary>
    public const string StealSlot = "ss";

    /// <summary>User pressed a cancel/dismiss button on a picker keyboard.</summary>
    public const string Dismiss = "x";

    /// <summary>DM selected a saved game from the unsave picker to delete.</summary>
    public const string UnsaveGame = "ug";

    /// <summary>DM confirmed proceeding with a vote despite scheduling collision.</summary>
    public const string VoteConfirm = "vco";

    /// <summary>DM aborted a vote after seeing a scheduling collision warning.</summary>
    public const string VoteAbort = "va";

    /// <summary>DM selected a campaign from the picker for /vote in a service thread (redirects to slot picker).</summary>
    public const string VoteCampaignPick = "vcp";

    /// <summary>User selected a campaign from the picker for /saved in a service thread.</summary>
    public const string SavedCampaignPick = "scp";

    /// <summary>DM selected a campaign from the picker for /unsave in a service thread.</summary>
    public const string UnsaveCampaignPick = "ucp";
}

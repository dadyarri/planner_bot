namespace PlannerBot.Services;

/// <summary>
/// Constants for callback query action prefixes used in inline keyboard buttons.
/// These values are the first segment of semicolon-delimited callback data strings.
/// </summary>
public static class CallbackActions
{
    /// <summary>User selected a day in the /plan calendar.</summary>
    public const string Plan = "plan";

    /// <summary>User selected an availability status (Yes/No/Probably) for a day.</summary>
    public const string PlanStatus = "pstatus";

    /// <summary>User selected a start time for a day.</summary>
    public const string PlanTime = "ptime";

    /// <summary>User pressed the "Back" button in the planning flow.</summary>
    public const string PlanBack = "pback";

    /// <summary>User pressed the "Done" button to finish planning.</summary>
    public const string PlanDone = "plan_done";

    /// <summary>Creator pressed the cancel button on a voting session.</summary>
    public const string VoteCancel = "vote_cancel";
}

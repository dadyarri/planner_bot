using Telegram.Bot.Types.Enums;

namespace PlannerBot.Services;

partial class UpdateHandler
{
    [LoggerMessage(LogLevel.Error, "HandleError")]
    static partial void LogHandleerrorException(ILogger<UpdateHandler> logger, Exception exception);

    [LoggerMessage(LogLevel.Information, "Received a message of type {messageType}")]
    static partial void LogReceivedAMessageOfTypeMessagetype(ILogger<UpdateHandler> logger, MessageType messageType);

    [LoggerMessage(LogLevel.Information, "Received callback query {cqCommand}")]
    static partial void LogReceivedCallbackQueryCqcommand(ILogger<UpdateHandler> logger, string cqCommand);

    [LoggerMessage(LogLevel.Information, "Received plan command")]
    static partial void LogReceivedPlanCommand(ILogger<UpdateHandler> logger);

    [LoggerMessage(LogLevel.Information, "Wrong user used plan command ({data} != {cq})")]
    static partial void LogWrongUserUsedPlanCommand(ILogger<UpdateHandler> logger, string data, string cq);

    [LoggerMessage(LogLevel.Information, "Received ptime command")]
    static partial void LogReceivedPtimeCommand(ILogger<UpdateHandler> logger);

    [LoggerMessage(LogLevel.Information, "Wrong user used ptime button ({data} != {cq})")]
    static partial void LogWrongUserUsedPtimeButtonDataCq(ILogger<UpdateHandler> logger, string data, string cq);

    [LoggerMessage(LogLevel.Information, "Received pback command")]
    static partial void LogReceivedPbackCommand(ILogger<UpdateHandler> logger);

    [LoggerMessage(LogLevel.Information, "Received command: {command} {args}")]
    static partial void LogReceivedCommandCommandArgs(ILogger<UpdateHandler> logger, string command, string args);

    [LoggerMessage(LogLevel.Information, "Unknown update type: {updateType}")]
    static partial void LogUnknownUpdateTypeUpdatetype(ILogger<UpdateHandler> logger, UpdateType updateType);

    [LoggerMessage(LogLevel.Information, "Reminder scheduled to {DateTime:yyyy-MM-dd HH:mm:ss}.")]
    static partial void LogReminderScheduledTo(ILogger<UpdateHandler> logger, DateTime dateTime);

    [LoggerMessage(LogLevel.Information, "Forum topic created: ChatId={ChatId}, ThreadId={ThreadId}")]
    static partial void LogForumTopicCreated(ILogger<UpdateHandler> logger, long chatId, int threadId);

    [LoggerMessage(LogLevel.Information, "Forum topic edited: ChatId={ChatId}, ThreadId={ThreadId}")]
    static partial void LogForumTopicEdited(ILogger<UpdateHandler> logger, long chatId, int threadId);

    [LoggerMessage(LogLevel.Information, "Forum topic closed: ChatId={ChatId}, ThreadId={ThreadId}")]
    static partial void LogForumTopicClosed(ILogger<UpdateHandler> logger, long chatId, int threadId);

    [LoggerMessage(LogLevel.Information, "Forum topic reopened: ChatId={ChatId}, ThreadId={ThreadId}")]
    static partial void LogForumTopicReopened(ILogger<UpdateHandler> logger, long chatId, int threadId);

    [LoggerMessage(LogLevel.Information,
        "Super-admin {SuperAdminUserId} picked campaign {CampaignId} for campaign join in chat {ChatId}")]
    static partial void LogSuperAdminPickedCampaignJoinTarget(
        ILogger<UpdateHandler> logger,
        long superAdminUserId,
        int campaignId,
        long chatId);

    [LoggerMessage(LogLevel.Information,
        "Super-admin {SuperAdminUserId} toggled user {TargetUserId} in campaign join draft for campaign {CampaignId} in chat {ChatId}")]
    static partial void LogSuperAdminToggledCampaignJoinUser(
        ILogger<UpdateHandler> logger,
        long superAdminUserId,
        long targetUserId,
        int campaignId,
        long chatId);

    [LoggerMessage(LogLevel.Warning,
        "Campaign join draft missing for super-admin {SuperAdminUserId} in chat {ChatId}")]
    static partial void LogMissingCampaignJoinDraft(
        ILogger<UpdateHandler> logger,
        long superAdminUserId,
        long chatId);

    [LoggerMessage(LogLevel.Warning,
        "Campaign join draft for super-admin {SuperAdminUserId} points to missing campaign {CampaignId} in chat {ChatId}")]
    static partial void LogCampaignJoinDraftTargetMissing(
        ILogger<UpdateHandler> logger,
        long superAdminUserId,
        int campaignId,
        long chatId);

    [LoggerMessage(LogLevel.Information,
        "Super-admin {SuperAdminUserId} saved campaign join draft for campaign {CampaignId} in chat {ChatId}; added {AddedCount}, reactivated {ReactivatedCount}")]
    static partial void LogSuperAdminSavedCampaignJoinDraft(
        ILogger<UpdateHandler> logger,
        long superAdminUserId,
        int campaignId,
        long chatId,
        int addedCount,
        int reactivatedCount);

    [LoggerMessage(LogLevel.Information,
        "Super-admin {SuperAdminUserId} assigned user {TargetUserId} as DM for a new campaign in chat {ChatId}, thread {ThreadId}")]
    static partial void LogSuperAdminAssignedCampaignDm(
        ILogger<UpdateHandler> logger,
        long superAdminUserId,
        long targetUserId,
        long chatId,
        int threadId);

    [LoggerMessage(LogLevel.Warning,
        "Callback owner {OwnerUserId} was not found for callback from Telegram user {CallbackUsername}")]
    static partial void LogCallbackOwnerMissing(
        ILogger<UpdateHandler> logger,
        long ownerUserId,
        string? callbackUsername);

    [LoggerMessage(LogLevel.Warning,
        "Callback ownership mismatch: owner user {OwnerUserId} has username {OwnerUsername}, callback came from {CallbackUsername}")]
    static partial void LogCallbackOwnerMismatch(
        ILogger<UpdateHandler> logger,
        long ownerUserId,
        string? ownerUsername,
        string? callbackUsername);
}

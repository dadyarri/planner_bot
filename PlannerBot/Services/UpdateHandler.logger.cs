using Telegram.Bot.Types.Enums;

namespace PlannerBot.Services;

partial class UpdateHandler
{
    [LoggerMessage(LogLevel.Error, "HandleError: {exception}")]
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
}
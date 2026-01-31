using Microsoft.Extensions.Logging;
using PlannerBot.Abstract;
using Telegram.Bot;

namespace PlannerBot.Services;

public class ReceiverService(ITelegramBotClient botClient, UpdateHandler updateHandler, ILogger<ReceiverServiceBase<UpdateHandler>> logger)
    : ReceiverServiceBase<UpdateHandler>(botClient, updateHandler, logger);
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace PlannerBot.Abstract;

/// <summary>An abstract class to compose Receiver Service and Update Handler classes</summary>
/// <typeparam name="TUpdateHandler">Update Handler to use in Update Receiver</typeparam>
public abstract class ReceiverServiceBase<TUpdateHandler>(ITelegramBotClient botClient, TUpdateHandler updateHandler, ILogger<ReceiverServiceBase<TUpdateHandler>> logger)
    : IReceiverService where TUpdateHandler : IUpdateHandler
{
    /// <summary>Start to service Updates with provided Update Handler class</summary>
    public async Task ReceiveAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions()
        {
            DropPendingUpdates = true,
            AllowedUpdates =
            [
                UpdateType.Message,
                UpdateType.CallbackQuery,
                UpdateType.MessageReaction
            ]
        };

        var me = await botClient.GetMe(stoppingToken);
        logger.LogInformation("Start receiving updates for {BotName}", me.Username ?? "My Awesome Bot");

        // Start receiving updates
        await botClient.ReceiveAsync(updateHandler, receiverOptions, stoppingToken);
    }
}
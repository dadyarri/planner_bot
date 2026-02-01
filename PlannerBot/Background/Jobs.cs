using Telegram.Bot;
using TickerQ.Utilities.Base;

namespace PlannerBot.Background;

public class Jobs(ILogger<Jobs> logger, ITelegramBotClient bot)
{
    [TickerFunction("send_reminder")]
    public async Task SendReminder(TickerFunctionContext<SendReminderJobContext> context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sending reminder");
        await bot.SendMessage(context.Request.ChatId, messageThreadId: context.Request.ThreadId,
            text: context.Request.Message, cancellationToken: cancellationToken);
    }
}
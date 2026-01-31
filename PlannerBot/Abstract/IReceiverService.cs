namespace PlannerBot.Abstract;

public interface IReceiverService
{
    Task ReceiveAsync(CancellationToken stoppingToken);
}
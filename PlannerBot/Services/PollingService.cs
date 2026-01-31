using Microsoft.Extensions.Logging;
using PlannerBot.Abstract;

namespace PlannerBot.Services;

public class PollingService(IServiceProvider serviceProvider, ILogger<PollingService> logger)
    : PollingServiceBase<ReceiverService>(serviceProvider, logger);
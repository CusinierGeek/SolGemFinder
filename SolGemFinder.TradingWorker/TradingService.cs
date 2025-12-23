using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class TradingService : BackgroundService
{
    private readonly ILogger<TradingService> _logger;
    private readonly IServiceProvider _services;

    public TradingService(ILogger<TradingService> logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TradingService starting.");

        // If a ServiceBusDiscoveryConsumer is registered (Service Bus mode), start it.
        var sbConsumer = _services.GetService(typeof(ServiceBusDiscoveryConsumer)) as ServiceBusDiscoveryConsumer;
        if (sbConsumer != null)
        {
            _logger.LogInformation("Starting ServiceBusDiscoveryConsumer...");
            try
            {
                sbConsumer.Start();
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to start ServiceBusDiscoveryConsumer");
            }
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        finally
        {
            if (sbConsumer != null)
            {
                _logger.LogInformation("Stopping ServiceBusDiscoveryConsumer...");
                try
                {
                    await sbConsumer.StopAsync();
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "Error stopping ServiceBusDiscoveryConsumer");
                }
            }

            _logger.LogInformation("TradingService stopping.");
        }
    }
}

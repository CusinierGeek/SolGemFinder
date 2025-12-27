using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolGemFinder.TradingWorker;

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
        _logger.LogInformation("?? TradingService starting...");

        // Start the new TradingSignalConsumer
        var tradingConsumer = _services.GetService<TradingSignalConsumer>();
        if (tradingConsumer != null)
        {
            _logger.LogInformation("?? Starting TradingSignalConsumer...");
            try
            {
                tradingConsumer.Start();
                _logger.LogInformation("? TradingSignalConsumer started successfully");
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "? Failed to start TradingSignalConsumer");
            }
        }
        else
        {
            _logger.LogWarning("?? TradingSignalConsumer not registered!");
        }

        try
        {
            _logger.LogInformation("?? TradingService is now listening for trading signals...");

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(10000, stoppingToken); // Log heartbeat every 10s
                _logger.LogDebug("?? TradingService heartbeat - Waiting for signals...");
            }
        }
        finally
        {
            if (tradingConsumer != null)
            {
                _logger.LogInformation("?? Stopping TradingSignalConsumer...");
                try
                {
                    await tradingConsumer.StopAsync();
                    _logger.LogInformation("? TradingSignalConsumer stopped");
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "? Error stopping TradingSignalConsumer");
                }
            }

            _logger.LogInformation("?? TradingService stopped");
        }
    }
}

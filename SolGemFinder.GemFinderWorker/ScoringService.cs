using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public interface IDiscoveryPublisher
{
    Task PublishAsync<T>(T payload, CancellationToken ct = default);
}

public class ScoringService : BackgroundService
{
    private readonly ILogger<ScoringService> _logger;
    private readonly IDiscoveryPublisher _publisher;
    private readonly ScoringConsumer? _consumer;

    public ScoringService(ILogger<ScoringService> logger, IDiscoveryPublisher publisher, ScoringConsumer? consumer = null)
    {
        _logger = logger;
        _publisher = publisher;
        _consumer = consumer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GemFinderService started (subscription-only mode).");

        if (_consumer != null)
        {
            _logger.LogInformation("Starting GemFinder consumer to receive indexer messages...");
            try
            {
                _consumer.Start();
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to start GemFinder consumer");
            }
        }

        try
        {
            // Wait until cancellation. No periodic publishing here; Indexer is the only publisher.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException) { }
        finally
        {
            if (_consumer != null)
            {
                _logger.LogInformation("Stopping GemFinder consumer...");
                try
                {
                    await _consumer.StopAsync();
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "Error stopping GemFinder consumer");
                }
            }

            _logger.LogInformation("GemFinderService stopping.");
        }
    }
}

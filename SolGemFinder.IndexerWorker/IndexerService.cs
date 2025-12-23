using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolGemFinder.Shared.Dtos;

public interface IIndexerPublisher
{
    Task PublishAsync<T>(T payload, CancellationToken ct = default);
}

public class IndexerService : BackgroundService
{
    private readonly ILogger<IndexerService> _logger;
    private readonly IIndexerPublisher _publisher;

    public IndexerService(ILogger<IndexerService> logger, IIndexerPublisher publisher)
    {
        _logger = logger;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IndexerService started (publishing test messages every 30s).");

        while (!stoppingToken.IsCancellationRequested)
        {
            var message = new DiscoveryDto { Id = System.Guid.NewGuid(), Source = "pump.fun", Timestamp = System.DateTime.UtcNow, Stage = "indexer", History = "Indexer:created" };
            try
            {
                _logger.LogInformation("Indexer publishing test message {Id} with history: {History}", message.Id, message.History);
                await _publisher.PublishAsync(message, stoppingToken);
                _logger.LogInformation("Indexer published test discovery {Id}.", message.Id);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error publishing discovery from indexer");
            }

            await Task.Delay(30_000, stoppingToken);
        }

        _logger.LogInformation("IndexerService stopping.");
    }
}

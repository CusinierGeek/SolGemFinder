using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public interface IDiscoveryPublisher
{
    Task PublishAsync<T>(T payload, CancellationToken ct = default);
}

public class GemFinderService : BackgroundService
{
    private readonly ILogger<GemFinderService> _logger;
    private readonly IDiscoveryPublisher _publisher;

    public GemFinderService(ILogger<GemFinderService> logger, IDiscoveryPublisher publisher)
    {
        _logger = logger;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GemFinderService started (publishing test messages every 30s).");

        while (!stoppingToken.IsCancellationRequested)
        {
            var message = new { Id = System.Guid.NewGuid(), Timestamp = System.DateTime.UtcNow };
            try
            {
                await _publisher.PublishAsync(message, stoppingToken);
                _logger.LogInformation("Published test discovery {Id}.", message.Id);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error publishing discovery");
            }

            await Task.Delay(30_000, stoppingToken);
        }

        _logger.LogInformation("GemFinderService stopping.");
    }
}

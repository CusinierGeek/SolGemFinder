using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolGemFinder.ScoringWorker.Services;

namespace SolGemFinder.ScoringWorker;

/// <summary>
/// Interface for publishing discoveries to downstream services.
/// </summary>
public interface IDiscoveryPublisher
{
    Task PublishAsync<T>(T payload, CancellationToken ct = default);
}

/// <summary>
/// Main background service for the Scoring Worker.
/// Manages ServiceBus consumer and watchlist monitoring.
/// </summary>
public class ScoringService : BackgroundService
{
    private readonly ILogger<ScoringService> _logger;
    private readonly ScoringConsumer _consumer;
    private readonly ITokenCacheService _cache;

    public ScoringService(
        ILogger<ScoringService> logger, 
        ScoringConsumer consumer,
        ITokenCacheService cache)
    {
        _logger = logger;
        _consumer = consumer;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("?? ScoringService starting...");

        // Check Redis health before starting
        var isHealthy = await _cache.IsHealthyAsync();
        if (!isHealthy)
        {
            _logger.LogCritical("? Redis is not healthy. Cannot start ScoringService.");
            throw new InvalidOperationException("Redis connection required");
        }

        var stats = await _cache.GetStatsAsync();
        _logger.LogInformation("?? Redis Stats:");
        _logger.LogInformation("   • Watchlist: {Count} tokens", stats.WatchlistCount);
        _logger.LogInformation("   • Blacklist: {Count} tokens", stats.BlacklistCount);
        _logger.LogInformation("   • Cached States: {Count}", stats.CachedStatesCount);
        _logger.LogInformation("   • Redis Version: {Version}", stats.RedisVersion ?? "unknown");

        // Start ServiceBus consumer
        try
        {
            _consumer.Start();
            _logger.LogInformation("? ServiceBus consumer started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Failed to start ServiceBus consumer");
            throw;
        }

        try
        {
            _logger.LogInformation("? ScoringService running - Press Ctrl+C to stop");
            
            // Wait until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            _logger.LogInformation("?? ScoringService stopping...");
            
            try
            {
                await _consumer.StopAsync();
                _logger.LogInformation("? ServiceBus consumer stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "? Error stopping ServiceBus consumer");
            }

            _logger.LogInformation("?? ScoringService stopped");
        }
    }
}

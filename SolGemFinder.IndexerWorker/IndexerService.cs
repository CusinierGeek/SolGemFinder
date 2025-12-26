using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolGemFinder.Shared.Dtos;
using SolGemFinder.IndexerWorker.Services;
using SolGemFinder.IndexerWorker.Models;

public interface IIndexerPublisher
{
    Task PublishAsync<T>(T payload, CancellationToken ct = default);
}

public class IndexerService : BackgroundService
{
    private readonly ILogger<IndexerService> _logger;
    private readonly IIndexerPublisher _publisher;
    private readonly ISolanaRadar _radar;
    private readonly IndexerServiceOptions _options;

    public IndexerService(
        ILogger<IndexerService> logger,
        IIndexerPublisher publisher,
        ISolanaRadar radar,
        IOptions<IndexerServiceOptions> options)
    {
        _logger = logger;
        _publisher = publisher;
        _radar = radar;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("?? IndexerService (Radar) started - Polling every {Interval}ms", _options.PollingIntervalMs);
        _logger.LogInformation("? Mode: ULTRA-FAST filtering (70-80% rejection rate)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var newTokens = await _radar.ScanForNewTokensAsync(stoppingToken);

                if (newTokens.Any())
                {
                    _logger.LogInformation("?? Radar found {Count} NEW token creation(s)", newTokens.Count);

                    foreach (var token in newTokens)
                    {
                        // Construire l'URL Pump.fun avec le bonding curve si disponible
                        string pumpFunUrl = !string.IsNullOrEmpty(token.BondingCurveAddress)
                            ? $"https://pump.fun/{token.BondingCurveAddress}"
                            : $"https://pump.fun/{token.TokenAddress}";

                        var discovery = new DiscoveryDto
                        {
                            Id = System.Guid.NewGuid(),
                            Source = "pump.fun",
                            Timestamp = token.Timestamp,
                            Stage = "indexer",
                            History = BuildTokenHistory(token, pumpFunUrl)
                        };

                        await _publisher.PublishAsync(discovery, stoppingToken);
                        _logger.LogInformation("?? Published: {TokenAddress} ? DetectionWorker | Pump.fun: {Url}",
                            token.TokenAddress, pumpFunUrl);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "? Error during radar scan");
            }

            await Task.Delay(_options.PollingIntervalMs, stoppingToken);
        }

        _logger.LogInformation("IndexerService stopping.");
    }

    private static string BuildTokenHistory(SolGemFinder.IndexerWorker.Models.PumpFunTokenInfo token, string pumpFunUrl)
    {
        var parts = new List<string>
        {
            "Indexer:created",
            $"Token:{token.TokenAddress}",
            $"PumpFun:{pumpFunUrl}",
            $"Tx:{token.TransactionSignature}",
            $"Slot:{token.Slot}",
            $"Time:{token.Timestamp:yyyy-MM-ddTHH:mm:ss}Z"
        };

        if (!string.IsNullOrEmpty(token.Creator))
            parts.Add($"Creator:{token.Creator}");

        if (!string.IsNullOrEmpty(token.BondingCurveAddress))
            parts.Add($"Bonding:{token.BondingCurveAddress}");

        return string.Join("|", parts);
    }
}

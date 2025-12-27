using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolGemFinder.Shared.Dtos;
using SolGemFinder.Shared.Services.BitQuery;
using SolGemFinder.IndexerWorker.Models;

public interface IIndexerPublisher
{
    Task PublishAsync<T>(T payload, CancellationToken ct = default);
}

public class IndexerService : BackgroundService
{
    private readonly ILogger<IndexerService> _logger;
    private readonly IIndexerPublisher _publisher;
    private readonly IBitQueryPumpFunService _bitQuery;
    private readonly IndexerServiceOptions _options;

    public IndexerService(
        ILogger<IndexerService> logger,
        IIndexerPublisher publisher,
        IBitQueryPumpFunService bitQuery,
        IOptions<IndexerServiceOptions> options)
    {
        _logger = logger;
        _publisher = publisher;
        _bitQuery = bitQuery;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("?? IndexerService (BitQuery) started - Polling every {Interval}ms", _options.PollingIntervalMs);
        _logger.LogInformation("?? Mode: TARGETED discovery (high-quality tokens only)");
        _logger.LogInformation("   Strategy: {Strategy}", _options.DiscoveryStrategy);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var newTokens = await _bitQuery.DiscoverTokensAsync(_options.DiscoveryStrategy, stoppingToken);

                if (newTokens.Any())
                {
                    _logger.LogInformation("?? BitQuery found {Count} high-quality token(s)", newTokens.Count);

                    foreach (var token in newTokens)
                    {
                        // Create rich TokenDiscoveryDto with all BitQuery data
                        var tokenDiscovery = new TokenDiscoveryDto
                        {
                            Id = System.Guid.NewGuid(),
                            Source = "bitquery-pumpfun",
                            Timestamp = token.Timestamp,
                            Strategy = _options.DiscoveryStrategy.ToString(),
                            TokenAddress = token.TokenAddress,
                            TransactionSignature = token.TransactionSignature,
                            Slot = token.Slot,
                            TokenName = token.TokenName,
                            TokenSymbol = token.TokenSymbol,
                            Creator = token.Creator,
                            BondingCurveAddress = token.BondingCurveAddress,
                            BondingCurveProgress = token.BondingCurveProgress,
                            IsPostBonding = token.IsPostBonding,
                            LiquidityInSOL = token.LiquidityInSOL,
                            LiquidityInUSD = token.LiquidityInUSD,
                            InitialSupply = token.InitialSupply
                        };

                        // Publish new format
                        await _publisher.PublishAsync(tokenDiscovery, stoppingToken);

                        _logger.LogInformation("?? Published: {Symbol} ({TokenAddress}) ? ScoringWorker | Quality: {Quality}% | Bonding: {Progress}% | Liquidity: {SOL} SOL",
                            tokenDiscovery.TokenSymbol ?? "Unknown",
                            tokenDiscovery.TokenAddress[..8],
                            tokenDiscovery.QualityScore,
                            tokenDiscovery.BondingCurveProgress?.ToString("F2") ?? "N/A",
                            tokenDiscovery.LiquidityInSOL?.ToString("F2") ?? "N/A");
                        
                        _logger.LogDebug("   ?? URLs: Pump.fun={PumpFun} | Solscan={Solscan}",
                            tokenDiscovery.PumpFunUrl,
                            tokenDiscovery.SolscanUrl);
                    }
                }
                else
                {
                    _logger.LogDebug("No new tokens discovered in this cycle");
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "? Error during BitQuery token discovery");
            }

            await Task.Delay(_options.PollingIntervalMs, stoppingToken);
        }

        _logger.LogInformation("IndexerService stopping.");
    }
}

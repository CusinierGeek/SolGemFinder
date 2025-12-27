using Azure.Messaging.ServiceBus;
using SolGemFinder.ScoringWorker.Models;
using SolGemFinder.ScoringWorker.Services;
using SolGemFinder.Shared.Dtos;
using System.Text.Json;

namespace SolGemFinder.ScoringWorker;

/// <summary>
/// Consumes token discoveries from IndexerWorker via ServiceBus.
/// Adds new tokens to Redis watchlist for monitoring.
/// </summary>
public class ScoringConsumer
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ScoringConsumer> _logger;
    private readonly ITokenCacheService _cache;
    private ServiceBusProcessor? _processor;
    private readonly string _topic;
    private readonly string _subscription;

    public ScoringConsumer(
        ServiceBusClient client,
        IConfiguration config,
        ILogger<ScoringConsumer> logger,
        ITokenCacheService cache)
    {
        _client = client;
        _logger = logger;
        _cache = cache;
        _topic = config.GetValue<string>("ServiceBus:Topic")
            ?? Environment.GetEnvironmentVariable("SERVICEBUS_TOPIC")
            ?? "discoveries";
        _subscription = config.GetValue<string>("ServiceBus:GemFinderSubscription")
            ?? Environment.GetEnvironmentVariable("SERVICEBUS_GEMFINDER_SUBSCRIPTION")
            ?? "gemfinder-sub";
    }

    /// <summary>
    /// Starts consuming messages from ServiceBus.
    /// </summary>
    public void Start()
    {
        if (_processor != null)
        {
            _logger.LogWarning("ScoringConsumer already started");
            return;
        }

        _processor = _client.CreateProcessor(_topic, _subscription, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 10
        });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        _processor.StartProcessingAsync();

        _logger.LogInformation("?? ScoringConsumer started - Topic: {Topic}, Subscription: {Subscription}",
            _topic, _subscription);
    }

    /// <summary>
    /// Stops the ServiceBus processor.
    /// </summary>
    public async Task StopAsync()
    {
        if (_processor != null)
        {
            _logger.LogInformation("?? Stopping ScoringConsumer...");
            await _processor.StopProcessingAsync();
            await _processor.DisposeAsync();
            _processor = null;
        }
    }

    /// <summary>
    /// Processes a single ServiceBus message containing a token discovery.
    /// </summary>
    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var body = args.Message.Body.ToString();

        try
        {
            // Deserialize as TokenDiscoveryDto (no legacy support)
            var tokenDiscovery = JsonSerializer.Deserialize<TokenDiscoveryDto>(body);

            if (tokenDiscovery == null)
            {
                _logger.LogWarning("?? Could not deserialize TokenDiscoveryDto from message");
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            _logger.LogInformation("?? Received: {Symbol} ({Mint}) | Source: {Source} | Strategy: {Strategy} | Quality: {Quality}%",
                tokenDiscovery.TokenSymbol ?? "Unknown",
                tokenDiscovery.TokenAddress[..8],
                tokenDiscovery.Source,
                tokenDiscovery.Strategy ?? "N/A",
                tokenDiscovery.QualityScore);

            // Check if already blacklisted
            var isBlacklisted = await _cache.IsBlacklistedAsync(tokenDiscovery.TokenAddress);
            if (isBlacklisted)
            {
                var reason = await _cache.GetBlacklistReasonAsync(tokenDiscovery.TokenAddress);
                _logger.LogInformation("? Token already blacklisted: {Mint} - Reason: {Reason}",
                    tokenDiscovery.TokenAddress[..8], reason);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            // Check if already in watchlist
            var existing = await _cache.GetFromWatchlistAsync(tokenDiscovery.TokenAddress);
            if (existing != null)
            {
                _logger.LogDebug("?? Token already in watchlist: {Mint}", tokenDiscovery.TokenAddress[..8]);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            // Create watchlist token
            var watchlistToken = new WatchlistToken
            {
                MintAddress = tokenDiscovery.TokenAddress,
                Signature = tokenDiscovery.TransactionSignature,
                Slot = tokenDiscovery.Slot,
                DetectedAt = tokenDiscovery.Timestamp,
                BondingCurveAddress = tokenDiscovery.BondingCurveAddress,
                Status = WatchlistStatus.Watching,
                LastUpdated = DateTime.UtcNow,
                CheckCount = 0
            };

            await _cache.AddToWatchlistAsync(watchlistToken);

            _logger.LogInformation("? Added to watchlist: {Symbol} ({Mint}) | Bonding: {Progress}% | Liquidity: {SOL} SOL | Quality: {Quality}%",
                tokenDiscovery.TokenSymbol ?? "Unknown",
                tokenDiscovery.TokenAddress[..8],
                tokenDiscovery.BondingCurveProgress?.ToString("F2") ?? "N/A",
                tokenDiscovery.LiquidityInSOL?.ToString("F2") ?? "N/A",
                tokenDiscovery.QualityScore);

            if (tokenDiscovery.IsNearGraduation)
            {
                _logger.LogInformation("?? HIGH PRIORITY: Token near graduation at {Progress}%!",
                    tokenDiscovery.BondingCurveProgress?.ToString("F2"));
            }

            var count = await _cache.GetWatchlistCountAsync();
            _logger.LogDebug("?? Watchlist size: {Count}", count);

            await args.CompleteMessageAsync(args.Message);
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError(jsonEx, "? Invalid JSON format in discovery message");
            await args.DeadLetterMessageAsync(args.Message, "InvalidFormat", "Could not deserialize TokenDiscoveryDto");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error processing discovery message");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    /// <summary>
    /// Handles ServiceBus processor errors.
    /// </summary>
    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "? ServiceBus error in ScoringConsumer - Source: {Source}",
            args.ErrorSource);
        return Task.CompletedTask;
    }
}

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolGemFinder.Shared.Dtos;

namespace SolGemFinder.TradingWorker;

/// <summary>
/// Consumes trading signals from ScoringWorker via ServiceBus.
/// Receives only tokens that passed scoring AND liquidity validation.
/// </summary>
public class TradingSignalConsumer
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<TradingSignalConsumer> _logger;
    private ServiceBusProcessor? _processor;
    private readonly string _topic;
    private readonly string _subscription;

    public TradingSignalConsumer(
        ServiceBusClient client,
        IConfiguration config,
        ILogger<TradingSignalConsumer> logger)
    {
        _client = client;
        _logger = logger;
        
        _topic = config.GetValue<string>("ServiceBus:TradingTopic")
            ?? Environment.GetEnvironmentVariable("SERVICEBUS_TRADING_TOPIC")
            ?? "trading-signals";
        
        _subscription = config.GetValue<string>("ServiceBus:TradingSubscription")
            ?? Environment.GetEnvironmentVariable("SERVICEBUS_TRADING_SUBSCRIPTION")
            ?? "trading-sub";
    }

    /// <summary>
    /// Starts consuming trading signals from ServiceBus.
    /// </summary>
    public void Start()
    {
        if (_processor != null)
        {
            _logger.LogWarning("TradingSignalConsumer already started");
            return;
        }

        _processor = _client.CreateProcessor(_topic, _subscription, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 5
        });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        _processor.StartProcessingAsync();

        _logger.LogInformation("?? TradingSignalConsumer started - Topic: {Topic}, Subscription: {Subscription}",
            _topic, _subscription);
    }

    /// <summary>
    /// Stops the ServiceBus processor.
    /// </summary>
    public async Task StopAsync()
    {
        if (_processor != null)
        {
            _logger.LogInformation("?? Stopping TradingSignalConsumer...");
            await _processor.StopProcessingAsync();
            await _processor.DisposeAsync();
            _processor = null;
        }
    }

    /// <summary>
    /// Processes a single trading signal message.
    /// </summary>
    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var body = args.Message.Body.ToString();

        try
        {
            var signal = JsonSerializer.Deserialize<TradingSignalDto>(body);

            if (signal == null)
            {
                _logger.LogWarning("?? Received null trading signal");
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            _logger.LogInformation("?? ============================================");
            _logger.LogInformation("?? TRADING SIGNAL RECEIVED");
            _logger.LogInformation("?? ============================================");
            _logger.LogInformation("?? Mint Address: {Mint}", signal.MintAddress);
            _logger.LogInformation("? Score: {Score}/100", signal.Score);
            _logger.LogInformation("?? Liquidity: {Liq} SOL", signal.LiquiditySol ?? 0);
            _logger.LogInformation("?? Volume 24h: {Vol} SOL", signal.Volume24hSol ?? 0);
            _logger.LogInformation("?? Recent Activity: {Active}", signal.HasRecentActivity ? "? Yes" : "? No");
            
            if (signal.Signals != null)
            {
                _logger.LogInformation("?? Scoring Breakdown:");
                _logger.LogInformation("   - On-Chain Validation: {Score}/30", signal.Signals.OnChainValidation);
                _logger.LogInformation("   - Holder Distribution: {Score}/25", signal.Signals.HolderDistribution);
                _logger.LogInformation("   - Liquidity Depth: {Score}/20", signal.Signals.LiquidityDepth);
                _logger.LogInformation("   - Trading Activity: {Score}/15", signal.Signals.TradingActivity);
                _logger.LogInformation("   - Timing: {Score}/10", signal.Signals.Timing);
            }

            _logger.LogInformation("? Detected At: {Time}", signal.DetectedAt);
            _logger.LogInformation("? Published At: {Time}", signal.PublishedAt);
            _logger.LogInformation("?? Time to Trade: {Duration:F0}s", (signal.PublishedAt - signal.DetectedAt).TotalSeconds);
            _logger.LogInformation("?? ============================================");

            // TODO: Implement actual trading logic
            // For now, just log the signal
            _logger.LogInformation("?? TODO: Implement buy/sell logic for: {Mint}", signal.MintAddress);

            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error processing trading signal");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    /// <summary>
    /// Handles ServiceBus processor errors.
    /// </summary>
    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "? ServiceBus error in TradingSignalConsumer - Source: {Source}",
            args.ErrorSource);
        return Task.CompletedTask;
    }
}

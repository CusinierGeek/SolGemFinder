using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SolGemFinder.Shared.Dtos;
using SolGemFinder.ScoringWorker.Models;

namespace SolGemFinder.ScoringWorker.Services;

/// <summary>
/// Publishes approved tokens to TradingWorker via ServiceBus.
/// Only tokens that passed scoring AND liquidity validation are published.
/// </summary>
public class TradingPublisher
{
    private readonly ILogger<TradingPublisher> _logger;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly string _topicName;

    public TradingPublisher(
        ILogger<TradingPublisher> logger,
        ServiceBusClient serviceBusClient,
        IConfiguration config)
    {
        _logger = logger;
        _serviceBusClient = serviceBusClient;
        _topicName = config.GetValue<string>("ServiceBus:TradingTopic") 
            ?? Environment.GetEnvironmentVariable("SERVICEBUS_TRADING_TOPIC")
            ?? "trading-signals";

        _logger.LogInformation("?? TradingPublisher initialized - Topic: {Topic}", _topicName);
    }

    /// <summary>
    /// Publishes a trading signal to the TradingWorker.
    /// </summary>
    public async Task PublishTradingSignalAsync(
        TokenScore score,
        LiquidityValidationResult liquidityResult)
    {
        try
        {
            var signal = new TradingSignalDto
            {
                Id = Guid.NewGuid(),
                MintAddress = score.MintAddress,
                Score = score.Score,
                LiquiditySol = liquidityResult.LiquiditySol,
                Volume24hSol = liquidityResult.Volume24hSol,
                HasRecentActivity = liquidityResult.HasRecentActivity,
                DetectedAt = score.ScoredAt,
                PublishedAt = DateTime.UtcNow,
                Source = "ScoringWorker",
                Signals = new SignalsDto
                {
                    OnChainValidation = score.Signals.OnChainValidation,
                    HolderDistribution = score.Signals.HolderDistribution,
                    LiquidityDepth = score.Signals.LiquidityDepth,
                    TradingActivity = score.Signals.TradingActivity,
                    Timing = score.Signals.Timing
                }
            };

            await PublishTradingSignalDirectAsync(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Failed to publish trading signal: {Mint}", score.MintAddress);
            throw;
        }
    }

    /// <summary>
    /// Publishes a trading signal directly (used for immediate signals like Pump.fun bonding).
    /// </summary>
    public async Task PublishTradingSignalDirectAsync(TradingSignalDto signal)
    {
        try
        {
            var sender = _serviceBusClient.CreateSender(_topicName);
            var json = JsonSerializer.Serialize(signal);
            var message = new ServiceBusMessage(json)
            {
                ContentType = "application/json",
                MessageId = signal.Id.ToString(),
                Subject = "TradingSignal"
            };

            // Add application properties for filtering
            message.ApplicationProperties["source"] = signal.Source;
            message.ApplicationProperties["mintAddress"] = signal.MintAddress;
            message.ApplicationProperties["score"] = signal.Score.ToString("F2");

            await sender.SendMessageAsync(message);

            _logger.LogInformation("??? Published trading signal: {Mint} | Score: {Score} | Liquidity: {Liq} SOL | Source: {Source}",
                signal.MintAddress, signal.Score, signal.LiquiditySol ?? 0, signal.Source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Failed to publish trading signal directly: {Mint}", signal.MintAddress);
            throw;
        }
    }
}

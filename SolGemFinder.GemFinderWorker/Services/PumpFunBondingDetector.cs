using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Solnet.Rpc;
using SolGemFinder.Shared.Services.BitQuery;

namespace SolGemFinder.ScoringWorker.Services;

/// <summary>
/// Service for detecting optimal Pump.fun bonding moments for trading signals.
/// Uses BitQuery for accurate bonding progress with RPC fallback.
/// </summary>
public class PumpFunBondingDetector
{
    #region Constants

    private const string PumpFunProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
    private const decimal BondingTargetSol = 85m; // Pump.fun bonds at ~85 SOL
    private const decimal OptimalBuyThreshold = 82m; // Buy when 82%+ (70 SOL)
    private const decimal EarlyBuyThreshold = 60m; // Early entry at 60%+ (51 SOL)

    #endregion

    #region Fields

    private readonly ILogger<PumpFunBondingDetector> _logger;
    private readonly IRpcClient? _rpcClient;
    private readonly IBitQueryPumpFunService _bitQuery;

    #endregion

    #region Constructor

    public PumpFunBondingDetector(
        ILogger<PumpFunBondingDetector> logger,
        IConfiguration config,
        IBitQueryPumpFunService bitQuery)
    {
        _logger = logger;
        _bitQuery = bitQuery;

        var rpcUrl = config.GetValue<string>("Solana:RpcUrl")
            ?? Environment.GetEnvironmentVariable("HELIUS_RPC_URL");

        if (!string.IsNullOrEmpty(rpcUrl))
        {
            _rpcClient = ClientFactory.GetClient(rpcUrl);
        }

        _logger.LogInformation("🎯 PumpFunBondingDetector initialized");
        _logger.LogInformation("   Optimal Threshold: {Threshold}%", OptimalBuyThreshold);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Checks if a Pump.fun token has reached optimal bonding conditions for trading.
    /// </summary>
    public async Task<BondingAnalysisResult?> AnalyzeBondingProgressAsync(string mintAddress, string? bondingCurveAddress = null)
    {
        try
        {
            _logger.LogDebug("🔍 Analyzing bonding progress for: {Mint}", mintAddress[..8]);


            BondingAnalysisResult? result = result = await AnalyzeWithBitQueryAsync(mintAddress, bondingCurveAddress);


            if (result == null)
            {
                _logger.LogDebug("⏭️ Could not analyze bonding progress (no data available)");
                return null;
            }

            // Log results
            if (result.ShouldTrade)
            {
                _logger.LogInformation("🚀 OPTIMAL BUY SIGNAL: {Mint} | Progress: {Progress:F1}% | Confidence: {Confidence:F0}% | Method: {Method}",
                    mintAddress[..8], result.BondingProgress, result.Confidence, result.UsedBitQuery ? "BitQuery" : "RPC");
            }
            else if (result.IsEarlyOpportunity)
            {
                _logger.LogInformation("⏰ EARLY OPPORTUNITY: {Mint} | Progress: {Progress:F1}% | Watch closely",
                    mintAddress[..8], result.BondingProgress);
            }
            else
            {
                _logger.LogDebug("📊 Bonding analysis: {Mint} | Progress: {Progress:F1}% | Not ready for trade",
                  mintAddress[..8], result.BondingProgress);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error analyzing bonding progress for {Mint}", mintAddress[..8]);
            return null;
        }
    }

    #endregion

    #region BitQuery Analysis

    private async Task<BondingAnalysisResult?> AnalyzeWithBitQueryAsync(string mintAddress, string? bondingCurveAddress)
    {
        _logger.LogDebug("  📊 Using BitQuery for bonding analysis");

        try
        {
            // Get bonding progress from BitQuery
            var bondingProgress = await _bitQuery.GetBondingProgressAsync(mintAddress);

            if (!bondingProgress.HasValue)
            {
                _logger.LogDebug("  ⏭️ No bonding data from BitQuery");
                return null;
            }

            _logger.LogInformation("  📊 BitQuery bonding progress: {Progress:F2}%", bondingProgress.Value);

            // Check if token has graduated
            var hasGraduated = await _bitQuery.HasGraduatedAsync(mintAddress);

            if (hasGraduated)
            {
                _logger.LogInformation("  ✅ Token has graduated to Raydium/Orca!");
                bondingProgress = 100m;
            }

            // Calculate SOL balance from progress
            // Progress = (Balance / 85) * 100
            // Balance = (Progress / 100) * 85
            var estimatedSolBalance = (bondingProgress.Value / 100m) * BondingTargetSol;

            // Determine trading signal
            var shouldBuy = bondingProgress >= OptimalBuyThreshold && bondingProgress < 100m;
            var isEarlyOpportunity = bondingProgress >= EarlyBuyThreshold && bondingProgress < OptimalBuyThreshold;

            return new BondingAnalysisResult
            {
                MintAddress = mintAddress,
                BondingCurveAddress = bondingCurveAddress ?? "Unknown",
                CurrentSolBalance = estimatedSolBalance,
                BondingProgress = bondingProgress.Value,
                ShouldTrade = shouldBuy,
                IsEarlyOpportunity = isEarlyOpportunity,
                HasGraduated = hasGraduated,
                EstimatedTimeToCompletion = EstimateTimeToCompletion(estimatedSolBalance),
                Confidence = CalculateConfidence(estimatedSolBalance, bondingProgress.Value),
                UsedBitQuery = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ BitQuery bonding analysis failed");
            return null;
        }
    }

    #endregion

    #region RPC Analysis (Fallback)


    #endregion

    #region Helper Methods

    /// <summary>
    /// Estimates time remaining until bonding completion based on current balance.
    /// </summary>
    private TimeSpan? EstimateTimeToCompletion(decimal currentBalance)
    {
        if (currentBalance >= BondingTargetSol)
            return TimeSpan.Zero;

        // Rough estimate: ~1 SOL per minute for popular tokens
        var remainingSol = BondingTargetSol - currentBalance;
        var estimatedMinutes = (int)Math.Ceiling((double)remainingSol);

        return TimeSpan.FromMinutes(estimatedMinutes);
    }

    /// <summary>
    /// Calculates confidence score for the trading signal.
    /// Higher confidence when closer to bonding completion.
    /// </summary>
    private decimal CalculateConfidence(decimal currentBalance, decimal progress)
    {
        // Base confidence from progress
        var baseConfidence = Math.Min(progress, 100m);

        // Boost confidence if we're in the optimal range (82-98%)
        if (progress >= OptimalBuyThreshold && progress <= 98m)
        {
            baseConfidence = Math.Min(baseConfidence + 15m, 100m);
        }

        // Reduce confidence if too early
        if (progress < EarlyBuyThreshold)
        {
            baseConfidence *= 0.6m;
        }

        return Math.Max(0m, Math.Min(100m, baseConfidence));
    }

    #endregion
}

#region Data Models

/// <summary>
/// Result of bonding curve analysis.
/// </summary>
public class BondingAnalysisResult
{
    public required string MintAddress { get; init; }
    public required string BondingCurveAddress { get; init; }
    public decimal CurrentSolBalance { get; init; }
    public decimal BondingProgress { get; init; }
    public bool ShouldTrade { get; init; }
    public bool IsEarlyOpportunity { get; init; }
    public bool HasGraduated { get; init; }
    public TimeSpan? EstimatedTimeToCompletion { get; init; }
    public decimal Confidence { get; init; }
    public bool UsedBitQuery { get; init; }
}

#endregion

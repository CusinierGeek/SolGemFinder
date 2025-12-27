using Microsoft.Extensions.Logging;
using SolGemFinder.ScoringWorker.Models;
using SolGemFinder.ScoringWorker.Services;

namespace SolGemFinder.ScoringWorker.Services;

/// <summary>
/// Engine for calculating token scores based on multiple signals.
/// Combines on-chain validation, holder distribution, liquidity, activity, and timing.
/// Uses BitQuery data from ValidationResult (no legacy MintData dependency).
/// </summary>
public class TokenScoringEngine
{
    private readonly ILogger<TokenScoringEngine> _logger;
    private readonly OnChainValidator _validator;

    public TokenScoringEngine(ILogger<TokenScoringEngine> logger, OnChainValidator validator)
    {
        _logger = logger;
        _validator = validator;
    }

    /// <summary>
    /// Calculates comprehensive score for a token.
    /// </summary>
    public async Task<TokenScore> CalculateScoreAsync(string mintAddress, ValidationResult validationResult, DateTime detectedAt)
    {
        _logger.LogInformation("?? Calculating score for: {Mint}", mintAddress[..8]);

        var signals = new ScoreSignals();
        var rejectionReasons = new List<string>();

        // Signal 1: On-Chain Validation (0-30 points)
        signals = signals with { OnChainValidation = CalculateOnChainValidationScore(validationResult, rejectionReasons) };

        // Signal 2: Holder Distribution (0-25 points)
        signals = signals with { HolderDistribution = CalculateHolderDistributionScore(validationResult, rejectionReasons) };

        // Signal 3: Liquidity Depth (0-20 points)
        signals = signals with { LiquidityDepth = CalculateLiquidityDepthScore(validationResult, rejectionReasons) };

        // Signal 4: Trading Activity (0-15 points)
        signals = signals with { TradingActivity = CalculateTradingActivityScore(validationResult, rejectionReasons) };

        // Signal 5: Timing (0-10 points)
        signals = signals with { Timing = CalculateTimingScore(detectedAt, validationResult, rejectionReasons) };

        var score = new TokenScore
        {
            MintAddress = mintAddress,
            Score = signals.Total,
            Signals = signals,
            RejectionReasons = rejectionReasons,
            ScoredAt = DateTime.UtcNow
        };

        _logger.LogInformation("?? Score calculated: {Symbol} ({Mint}) = {Score}/100 (OnChain:{OC}, Holder:{H}, Liquidity:{L}, Activity:{A}, Timing:{T})",
            validationResult.TokenSymbol ?? "Unknown", mintAddress[..8], score.Score, 
            signals.OnChainValidation, signals.HolderDistribution, 
            signals.LiquidityDepth, signals.TradingActivity, signals.Timing);

        if (score.IsRecommended)
        {
            _logger.LogInformation("? Token recommended for trading: {Symbol} ({Mint}) (Score: {Score})", 
                validationResult.TokenSymbol ?? "Unknown", mintAddress[..8], score.Score);
        }
        else
        {
            _logger.LogWarning("? Token NOT recommended: {Mint} (Score: {Score}) - Reasons: {Reasons}",
                mintAddress[..8], score.Score, string.Join(", ", rejectionReasons));
        }

        return score;
    }

    #region Signal Calculations

    /// <summary>
    /// Signal 1: On-Chain Validation (0-30 points)
    /// Uses BitQuery metadata validation instead of RPC mint data.
    /// </summary>
    private decimal CalculateOnChainValidationScore(ValidationResult validation, List<string> rejectionReasons)
    {
        decimal score = 0;

        // Check 1: Token has metadata (10 points)
        if (!string.IsNullOrEmpty(validation.TokenName) || !string.IsNullOrEmpty(validation.TokenSymbol))
        {
            score += 10;
            _logger.LogDebug("  ? Token has metadata: +10");
        }
        else
        {
            rejectionReasons.Add("No token metadata (name/symbol)");
            _logger.LogDebug("  ? No metadata: 0");
        }

        // Check 2: Metadata is not mutable (10 points)
        // Mutable metadata = security risk (can be changed by authority)
        if (!validation.IsMutable)
        {
            score += 10;
            _logger.LogDebug("  ? Metadata immutable: +10");
        }
        else
        {
            rejectionReasons.Add("Metadata is mutable (security risk)");
            _logger.LogDebug("  ?? Metadata mutable: 0");
        }

        // Check 3: For Pump.fun tokens, check bonding status (5 points)
        if (validation.IsPumpFunToken)
        {
            if (validation.BondingProgress.HasValue)
            {
                score += 5;
                _logger.LogDebug("  ? Has bonding data: +5");
            }
            else
            {
                _logger.LogDebug("  ?? No bonding data: 0");
            }
        }
        else
        {
            // Not a Pump.fun token - give neutral score
            score += 3;
            _logger.LogDebug("  ? Non-Pump.fun token: +3");
        }

        // Check 4: Has trading activity (5 points)
        if (validation.RecentTradeCount > 0)
        {
            score += 5;
            _logger.LogDebug("  ? Has trading history: +5");
        }
        else
        {
            rejectionReasons.Add("No trading history");
            _logger.LogDebug("  ? No trades: 0");
        }

        _logger.LogDebug("  ?? On-Chain Validation: {Score}/30", score);
        return score;
    }

    /// <summary>
    /// Signal 2: Holder Distribution (0-25 points)
    /// Checks concentration and distribution of holders.
    /// </summary>
    private decimal CalculateHolderDistributionScore(ValidationResult validation, List<string> rejectionReasons)
    {
        decimal score = 0;

        // Top holder concentration (15 points max)
        if (validation.TopHoldersPercentage > 0)
        {
            if (validation.TopHoldersPercentage < 20)
            {
                score += 15;
                _logger.LogDebug("  ? Excellent distribution (<20%): +15");
            }
            else if (validation.TopHoldersPercentage < 30)
            {
                score += 10;
                _logger.LogDebug("  ? Good distribution (<30%): +10");
            }
            else if (validation.TopHoldersPercentage < 40)
            {
                score += 5;
                _logger.LogDebug("  ?? Moderate concentration (<40%): +5");
            }
            else if (validation.TopHoldersPercentage < 50)
            {
                score += 2;
                rejectionReasons.Add($"High concentration: Top holder = {validation.TopHoldersPercentage:F1}%");
                _logger.LogDebug("  ?? High concentration (<50%): +2");
            }
            else
            {
                rejectionReasons.Add($"Very high concentration: Top holder = {validation.TopHoldersPercentage:F1}%");
                _logger.LogDebug("  ? Very high concentration (>50%): 0");
            }
        }
        else
        {
            // No holder data yet
            score += 5;
            _logger.LogDebug("  ?? No holder data yet: +5 (neutral)");
        }

        // Holder count (10 points max)
        if (validation.HolderCount >= 50)
        {
            score += 10;
            _logger.LogDebug("  ? Many holders (?50): +10");
        }
        else if (validation.HolderCount >= 20)
        {
            score += 7;
            _logger.LogDebug("  ? Good holders (?20): +7");
        }
        else if (validation.HolderCount >= 10)
        {
            score += 5;
            _logger.LogDebug("  ?? Few holders (?10): +5");
        }
        else if (validation.HolderCount >= 5)
        {
            score += 3;
            _logger.LogDebug("  ?? Very few holders (?5): +3");
        }
        else if (validation.HolderCount > 0)
        {
            score += 1;
            _logger.LogDebug("  ?? Minimal holders: +1");
        }

        _logger.LogDebug("  ?? Holder Distribution: {Score}/25", score);
        return score;
    }

    /// <summary>
    /// Signal 3: Liquidity Depth (0-20 points)
    /// Evaluates pool liquidity and bonding status.
    /// </summary>
    private decimal CalculateLiquidityDepthScore(ValidationResult validation, List<string> rejectionReasons)
    {
        decimal score = 0;

        // Pool created / graduated (10 points)
        if (validation.IsBondingCompleted && !string.IsNullOrEmpty(validation.PoolType))
        {
            score += 10;
            _logger.LogDebug("  ? Pool created ({Type}): +10", validation.PoolType);

            // Raydium/Orca gets bonus (more established)
            if (validation.PoolType?.Contains("Raydium", StringComparison.OrdinalIgnoreCase) == true ||
                validation.PoolType?.Contains("Orca", StringComparison.OrdinalIgnoreCase) == true)
            {
                score += 5;
                _logger.LogDebug("  ? Premium DEX pool: +5");
            }
            else
            {
                score += 3;
                _logger.LogDebug("  ? DEX pool: +3");
            }
        }
        else if (validation.IsPumpFunToken && validation.BondingProgress.HasValue)
        {
            // Still in bonding - score based on progress
            var progress = validation.BondingProgress.Value;
            
            if (progress >= 95m)
            {
                score += 8;
                _logger.LogDebug("  ?? Near graduation (?95%): +8");
            }
            else if (progress >= 85m)
            {
                score += 6;
                _logger.LogDebug("  ? High bonding (?85%): +6");
            }
            else if (progress >= 70m)
            {
                score += 4;
                _logger.LogDebug("  ? Moderate bonding (?70%): +4");
            }
            else
            {
                score += 2;
                rejectionReasons.Add($"Low bonding progress: {progress:F1}%");
                _logger.LogDebug("  ?? Low bonding: +2");
            }
        }
        else
        {
            rejectionReasons.Add("No liquidity pool or bonding data");
            _logger.LogDebug("  ? No pool/bonding: 0");
        }

        // Volume check (5 points) - if available
        if (validation.Volume24hUSD.HasValue)
        {
            var volume = validation.Volume24hUSD.Value;
            
            if (volume >= 10000m)
            {
                score += 5;
                _logger.LogDebug("  ? High volume (?$10k): +5");
            }
            else if (volume >= 5000m)
            {
                score += 3;
                _logger.LogDebug("  ? Good volume (?$5k): +3");
            }
            else if (volume >= 1000m)
            {
                score += 2;
                _logger.LogDebug("  ?? Moderate volume (?$1k): +2");
            }
            else
            {
                _logger.LogDebug("  ?? Low volume: +1");
                score += 1;
            }
        }
        else
        {
            // No volume data yet - neutral
            score += 2;
            _logger.LogDebug("  ?? No volume data: +2");
        }

        _logger.LogDebug("  ?? Liquidity Depth: {Score}/20", score);
        return score;
    }

    /// <summary>
    /// Signal 4: Trading Activity (0-15 points)
    /// Measures recent trading and activity momentum.
    /// </summary>
    private decimal CalculateTradingActivityScore(ValidationResult validation, List<string> rejectionReasons)
    {
        decimal score = 0;

        // Recent activity (last 15 min) - 8 points
        if (validation.HasRecentActivity)
        {
            score += 8;
            _logger.LogDebug("  ? Recent activity (15min): +8");
        }
        else
        {
            rejectionReasons.Add("No recent activity (15min)");
            _logger.LogDebug("  ?? No recent activity: 0");
        }

        // Trade count (7 points max)
        if (validation.RecentTradeCount >= 20)
        {
            score += 7;
            _logger.LogDebug("  ? High trade count (?20): +7");
        }
        else if (validation.RecentTradeCount >= 10)
        {
            score += 5;
            _logger.LogDebug("  ? Good trade count (?10): +5");
        }
        else if (validation.RecentTradeCount >= 5)
        {
            score += 3;
            _logger.LogDebug("  ?? Moderate trades (?5): +3");
        }
        else if (validation.RecentTradeCount > 0)
        {
            score += 1;
            _logger.LogDebug("  ?? Few trades: +1");
        }

        _logger.LogDebug("  ?? Trading Activity: {Score}/15", score);
        return score;
    }

    /// <summary>
    /// Signal 5: Timing (0-10 points)
    /// Rewards early discovery and quick bonding/graduation.
    /// </summary>
    private decimal CalculateTimingScore(DateTime detectedAt, ValidationResult validation, List<string> rejectionReasons)
    {
        decimal score = 0;

        var timeToValidation = DateTime.UtcNow - detectedAt;

        // Quick bonding/graduation (5 points)
        if (validation.IsBondingCompleted)
        {
            if (timeToValidation.TotalMinutes < 5)
            {
                score += 5;
                _logger.LogDebug("  ?? Very fast graduation (<5min): +5");
            }
            else if (timeToValidation.TotalMinutes < 15)
            {
                score += 4;
                _logger.LogDebug("  ? Fast graduation (<15min): +4");
            }
            else if (timeToValidation.TotalMinutes < 30)
            {
                score += 3;
                _logger.LogDebug("  ? Moderate time (<30min): +3");
            }
            else if (timeToValidation.TotalMinutes < 60)
            {
                score += 2;
                _logger.LogDebug("  ?? Slower (<60min): +2");
            }
            else
            {
                score += 1;
                _logger.LogDebug("  ?? Slow (>60min): +1");
            }
        }
        else if (validation.IsPumpFunToken && validation.BondingProgress.HasValue)
        {
            // Still bonding - score based on progress speed
            var progress = validation.BondingProgress.Value;
            var minutesPerPercent = timeToValidation.TotalMinutes / (double)progress;
            
            if (minutesPerPercent < 0.5) // <30 sec per 1%
            {
                score += 4;
                _logger.LogDebug("  ?? Fast bonding speed: +4");
            }
            else if (minutesPerPercent < 1.0) // <1 min per 1%
            {
                score += 3;
                _logger.LogDebug("  ? Good bonding speed: +3");
            }
            else
            {
                score += 1;
                _logger.LogDebug("  ?? Moderate speed: +1");
            }
        }

        // Early detection bonus (5 points)
        if (timeToValidation.TotalMinutes < 5)
        {
            score += 5;
            _logger.LogDebug("  ?? Very early detection (<5min): +5");
        }
        else if (timeToValidation.TotalMinutes < 10)
        {
            score += 4;
            _logger.LogDebug("  ? Early detection (<10min): +4");
        }
        else if (timeToValidation.TotalMinutes < 30)
        {
            score += 3;
            _logger.LogDebug("  ? Good timing (<30min): +3");
        }
        else if (timeToValidation.TotalMinutes < 60)
        {
            score += 2;
            _logger.LogDebug("  ?? Moderate timing: +2");
        }
        else
        {
            score += 1;
            _logger.LogDebug("  ?? Late detection: +1");
        }

        _logger.LogDebug("  ?? Timing: {Score}/10", score);
        return score;
    }

    #endregion
}

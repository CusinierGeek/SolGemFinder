using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SolGemFinder.ScoringWorker.Models;
using SolGemFinder.ScoringWorker.Services;

namespace SolGemFinder.ScoringWorker;

/// <summary>
/// Background service that monitors the watchlist and checks token status.
/// Polls Redis watchlist every X seconds and decides:
/// - Keep watching (still pre-bonding)
/// - Move to scoring (bonding completed)
/// - Blacklist (invalid/dead)
/// - Expire (timeout)
/// </summary>
public class WatchlistMonitor : BackgroundService
{
    private readonly ILogger<WatchlistMonitor> _logger;
    private readonly ITokenCacheService _cache;
    private readonly OnChainValidator _validator;
    private readonly TokenScoringEngine _scoringEngine;
    private readonly LiquidityValidator _liquidityValidator;
    private readonly TradingPublisher _tradingPublisher;
    private readonly PumpFunBondingDetector _bondingDetector;
    private readonly IConfiguration _config;
    private readonly int _pollingIntervalSeconds;
    private readonly int _maxWatchlistAgeHours = 24;
    private readonly int _gracePeriodSeconds;
    private readonly int _maxRetries;

    public WatchlistMonitor(
        ILogger<WatchlistMonitor> logger,
        ITokenCacheService cache,
        OnChainValidator validator,
        TokenScoringEngine scoringEngine,
        LiquidityValidator liquidityValidator,
        TradingPublisher tradingPublisher,
        PumpFunBondingDetector bondingDetector,
        IConfiguration config)
    {
        _logger = logger;
        _cache = cache;
        _validator = validator;
        _scoringEngine = scoringEngine;
        _liquidityValidator = liquidityValidator;
        _tradingPublisher = tradingPublisher;
        _bondingDetector = bondingDetector;
        _config = config;
        _pollingIntervalSeconds = config.GetValue<int>("Scoring:WatchlistPollingIntervalSeconds", 30);
        _gracePeriodSeconds = config.GetValue<int>("Scoring:TokenGracePeriodSeconds", 60);
        _maxRetries = config.GetValue<int>("Scoring:MaxValidationRetries", 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("??? WatchlistMonitor started - Polling every {Interval}s", _pollingIntervalSeconds);

        // Wait a bit for services to initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorWatchlistAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "? Error in WatchlistMonitor cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_pollingIntervalSeconds), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Normal shutdown
                break;
            }
        }

        _logger.LogInformation("??? WatchlistMonitor stopped");
    }

    /// <summary>
    /// Monitors all tokens in the watchlist and updates their status.
    /// </summary>
    private async Task MonitorWatchlistAsync(CancellationToken ct)
    {
        var tokens = await _cache.GetAllWatchlistTokensAsync();

        if (tokens.Count == 0)
        {
            _logger.LogTrace("?? Watchlist is empty");
            return;
        }

        _logger.LogInformation("?? Monitoring {Count} tokens in watchlist", tokens.Count);

        int kept = 0;
        int bondingCompleted = 0;
        int blacklisted = 0;
        int expired = 0;

        foreach (var token in tokens)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var action = await CheckTokenStatusAsync(token);

                switch (action)
                {
                    case WatchlistAction.KeepWatching:
                        kept++;
                        await UpdateTokenCheckCountAsync(token);
                        break;

                    case WatchlistAction.BondingCompleted:
                        bondingCompleted++;
                        await MoveToBondingCompletedAsync(token);
                        break;

                    case WatchlistAction.Blacklist:
                        blacklisted++;
                        await BlacklistTokenAsync(token, "Failed validation checks");
                        break;

                    case WatchlistAction.Expire:
                        expired++;
                        await ExpireTokenAsync(token);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "? Error checking token: {Mint}", token.MintAddress);
            }
        }

        _logger.LogInformation("?? Watchlist cycle complete: Kept={Kept}, BondingDone={Done}, Blacklisted={Black}, Expired={Exp}",
            kept, bondingCompleted, blacklisted, expired);
    }

    /// <summary>
    /// Checks the current status of a token and decides what action to take.
    /// </summary>
    private async Task<WatchlistAction> CheckTokenStatusAsync(WatchlistToken token)
    {
        // Check if expired (too old)
        var age = DateTime.UtcNow - token.DetectedAt;
        if (age.TotalHours > _maxWatchlistAgeHours)
        {
            _logger.LogDebug("? Token expired: {Mint} (Age: {Hours:F1}h)", token.MintAddress, age.TotalHours);
            return WatchlistAction.Expire;
        }

        // Grace period: Wait at least X seconds before first validation
        // Tokens need time to propagate on Solana network
        if (token.CheckCount == 0 && age.TotalSeconds < _gracePeriodSeconds)
        {
            var remainingSeconds = _gracePeriodSeconds - (int)age.TotalSeconds;
            _logger.LogDebug("⏰ Grace period: {Mint} - Wait {Seconds}s before first check",
                token.MintAddress, remainingSeconds);

            // Update check count to mark it as "waiting"
            await UpdateTokenCheckCountAsync(token);
            return WatchlistAction.KeepWatching;
        }

        // Check if we've exceeded max retries
        if (token.CheckCount >= _maxRetries)
        {
            _logger.LogWarning("⚠️ Max retries exceeded: {Mint} - Checks: {Count}, Age: {Age:F0}s",
                token.MintAddress, token.CheckCount, age.TotalSeconds);
            return WatchlistAction.Blacklist;
        }

        // Perform on-chain validation
        _logger.LogDebug("🔍 Validating token: {Mint} (Check #{Count}, Age: {Age:F0}s)",
            token.MintAddress, token.CheckCount + 1, age.TotalSeconds);

        var validationResult = await _validator.ValidateTokenAsync(token.MintAddress);

        // Cache the validation result
        await CacheValidationResultAsync(token.MintAddress, validationResult);

        // If validation failed, check if it's a permanent or temporary error
        if (!validationResult.IsValid)
        {
            bool isPermanentError = IsPermanentValidationError(validationResult);

            if (isPermanentError)
            {
                // Hard reject: mint authority not revoked, freeze authority not revoked, etc.
                _logger.LogWarning("⛔ PERMANENT validation failure: {Mint} - Errors: {Errors}",
                    token.MintAddress,
                    string.Join(", ", validationResult.Errors));

                return WatchlistAction.Blacklist;
            }
            else
            {
                // Temporary error (token not found, RPC issue, etc.) - retry with backoff
                _logger.LogDebug("⚠ TEMPORARY validation failure: {Mint} - Errors: {Errors} | Retry #{Count}/{Max}",
                    token.MintAddress,
                    string.Join(", ", validationResult.Errors),
                    token.CheckCount + 1,
                    _maxRetries);

                return WatchlistAction.KeepWatching;
            }
        }

        // 🎯 NEW: Check Pump.fun bonding progress for optimal buy signal
        if (validationResult.IsPumpFunToken && !string.IsNullOrEmpty(token.BondingCurveAddress))
        {
            var bondingAnalysis = await _bondingDetector.AnalyzeBondingProgressAsync(
                token.MintAddress,
                token.BondingCurveAddress);

            if (bondingAnalysis != null && bondingAnalysis.ShouldTrade)
            {
                _logger.LogInformation("🚀 OPTIMAL BUY SIGNAL DETECTED: {Mint} | Progress: {Progress:F1}% | Confidence: {Confidence:F0}%",
                    token.MintAddress, bondingAnalysis.BondingProgress, bondingAnalysis.Confidence);

                // Send trading signal immediately for Pump.fun tokens at optimal bonding point
                await SendPumpFunTradingSignalAsync(token, bondingAnalysis, validationResult);
            }
            else if (bondingAnalysis != null && bondingAnalysis.IsEarlyOpportunity)
            {
                _logger.LogInformation("⏰ Early opportunity detected: {Mint} | Progress: {Progress:F1}%",
                    token.MintAddress, bondingAnalysis.BondingProgress);
            }
        }

        // If bonding completed (migrated to Raydium), move to scoring
        if (validationResult.IsBondingCompleted)
        {
            _logger.LogInformation("✅ Bonding completed: {Mint} | Pool: {Pool}",
                token.MintAddress,
                validationResult.PoolType ?? "Unknown");

            return WatchlistAction.BondingCompleted;
        }

        // Otherwise, keep watching
        _logger.LogTrace("👁 Still watching: {Mint} (Checks: {Count})", token.MintAddress, token.CheckCount + 1);
        return WatchlistAction.KeepWatching;
    }

    /// <summary>
    /// Determines if a validation error is permanent (hard reject) or temporary (retry).
    /// </summary>
    private bool IsPermanentValidationError(ValidationResult validationResult)
    {
        // Permanent errors that should lead to immediate blacklist
        var permanentErrors = new[]
        {
            "Mint authority not revoked",
            "Freeze authority not revoked",
            "Not a valid SPL token",
            "Invalid supply (zero)",
            "Suspicious decimals"
        };

        return validationResult.Errors.Any(error =>
            permanentErrors.Any(permanent => error.Contains(permanent, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Caches validation result in Redis.
    /// </summary>
    private async Task CacheValidationResultAsync(string mintAddress, ValidationResult validationResult)
    {
        var tokenState = new TokenState
        {
            MintAddress = mintAddress,
            IsBondingCompleted = validationResult.IsBondingCompleted,
            IsMintAuthorityRevoked = true, // Assumed for BitQuery tokens (Pump.fun managed)
            IsFreezeAuthorityRevoked = true, // Assumed for BitQuery tokens (Pump.fun managed)
            TotalSupply = null, // Not available from BitQuery metadata API
            Decimals = 6, // Default for Pump.fun tokens
            PoolType = validationResult.PoolType,
            HolderCount = validationResult.HolderCount,
            LastUpdated = DateTime.UtcNow,
            IsValid = validationResult.IsValid,
            ValidationErrors = validationResult.Errors.Count > 0 ? validationResult.Errors : null
        };

        await _cache.CacheTokenStateAsync(tokenState);
    }

    /// <summary>
    /// Updates the check count for a token.
    /// </summary>
    private async Task UpdateTokenCheckCountAsync(WatchlistToken token)
    {
        var updated = token with
        {
            CheckCount = token.CheckCount + 1,
            LastUpdated = DateTime.UtcNow
        };

        await _cache.UpdateWatchlistTokenAsync(updated);
        _logger.LogTrace("?? Updated check count: {Mint} ? {Count}", token.MintAddress, updated.CheckCount);
    }

    /// <summary>
    /// Marks a token as bonding completed and ready for scoring.
    /// Calculates final score and decides if should be sent to trading.
    /// </summary>
    private async Task MoveToBondingCompletedAsync(WatchlistToken token)
    {
        try
        {
            _logger.LogInformation("?? Bonding completed: {Mint} | Starting scoring process...", token.MintAddress);

            // Get cached validation result
            var cachedState = await _cache.GetCachedTokenStateAsync(token.MintAddress);
            if (cachedState == null)
            {
                _logger.LogWarning("?? No cached state found for scoring: {Mint}", token.MintAddress);
                return;
            }

            // Reconstruct validation result from cached state
            var validationResult = new ValidationResult
            {
                MintAddress = token.MintAddress,
                IsValid = cachedState.IsValid,
                IsBondingCompleted = cachedState.IsBondingCompleted,
                PoolType = cachedState.PoolType,
                HolderCount = cachedState.HolderCount ?? 0,
                TopHoldersPercentage = 0, // TODO: Get from cache if available

            };

            // STEP 1: Calculate score
            var score = await _scoringEngine.CalculateScoreAsync(
                token.MintAddress,
                validationResult,
                token.DetectedAt
            );

            // Cache the score
            await _cache.CacheScoreAsync(score);

            // Update token status
            var updated = token with
            {
                Status = score.IsRecommended ? WatchlistStatus.SentToTrading : WatchlistStatus.BondingCompleted,
                LastUpdated = DateTime.UtcNow
            };

            await _cache.UpdateWatchlistTokenAsync(updated);

            if (score.IsRecommended)
            {
                _logger.LogInformation("? Token APPROVED by scoring: {Mint} | Score: {Score}/100",
                    token.MintAddress, score.Score);

                // STEP 2: Validate liquidity & volume (final check before trading)
                _logger.LogInformation("?? Validating liquidity & volume: {Mint}", token.MintAddress);

                var liquidityResult = await _liquidityValidator.ValidateLiquidityAsync(
                    token.MintAddress,
                    token.BondingCurveAddress); // Or pool address if available

                if (liquidityResult.IsValid)
                {
                    _logger.LogInformation("? Liquidity validation PASSED: {Mint} | Liquidity: {Liq} SOL | Volume: {Vol} SOL",
                        token.MintAddress, liquidityResult.LiquiditySol ?? 0, liquidityResult.Volume24hSol ?? 0);

                    // STEP 3: Publish to TradingWorker
                    try
                    {
                        await _tradingPublisher.PublishTradingSignalAsync(score, liquidityResult);

                        _logger.LogInformation("?? ? FINAL APPROVAL: {Mint} sent to TradingWorker!", token.MintAddress);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "? Failed to publish to TradingWorker: {Mint}", token.MintAddress);
                    }
                }
                else
                {
                    _logger.LogWarning("? Liquidity validation FAILED: {Mint} - Errors: {Errors}",
                        token.MintAddress, string.Join(", ", liquidityResult.Errors));
                    _logger.LogWarning("?? Token rejected after liquidity check: {Mint}", token.MintAddress);
                }
            }
            else
            {
                _logger.LogWarning("? Token rejected (low score): {Mint} | Score: {Score}/100 | Reasons: {Reasons}",
                    token.MintAddress, score.Score, string.Join(", ", score.RejectionReasons));
            }

            // Remove from active watchlist (either sent to trading or rejected)
            await _cache.RemoveFromWatchlistAsync(token.MintAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error scoring token: {Mint}", token.MintAddress);
        }
    }

    /// <summary>
    /// Blacklists a token and removes it from watchlist.
    /// </summary>
    private async Task BlacklistTokenAsync(WatchlistToken token, string reason)
    {
        // If no specific reason provided, use token's rejection reason or default
        var blacklistReason = !string.IsNullOrEmpty(reason) ? reason :
            !string.IsNullOrEmpty(token.RejectionReason) ? token.RejectionReason :
            token.CheckCount >= _maxRetries ? $"Max retries exceeded ({token.CheckCount} attempts)" :
            "Failed validation checks";

        await _cache.BlacklistTokenAsync(token.MintAddress, blacklistReason);
        await _cache.RemoveFromWatchlistAsync(token.MintAddress);

        _logger.LogWarning("?? Blacklisted: {Mint} - Reason: {Reason} (Age: {Age:F0}s, Checks: {Count})",
            token.MintAddress, blacklistReason, (DateTime.UtcNow - token.DetectedAt).TotalSeconds, token.CheckCount);
    }

    /// <summary>
    /// Expires a token that has been in watchlist too long without bonding.
    /// </summary>
    private async Task ExpireTokenAsync(WatchlistToken token)
    {
        var updated = token with
        {
            Status = WatchlistStatus.Expired,
            LastUpdated = DateTime.UtcNow
        };

        await _cache.UpdateWatchlistTokenAsync(updated);
        await _cache.RemoveFromWatchlistAsync(token.MintAddress);

        _logger.LogInformation("⏱ Expired: {Mint} | Age: {Hours:F1}h without bonding",
            token.MintAddress,
            (DateTime.UtcNow - token.DetectedAt).TotalHours);
    }

    /// <summary>
    /// Sends an immediate trading signal for Pump.fun tokens at optimal bonding point.
    /// </summary>
    private async Task SendPumpFunTradingSignalAsync(
        WatchlistToken token,
        BondingAnalysisResult bondingAnalysis,
        ValidationResult validationResult)
    {
        try
        {
            _logger.LogInformation("📤 Sending Pump.fun trading signal: {Mint}", token.MintAddress);

            // Quick scoring for Pump.fun tokens (no need for full liquidity validation yet)
            var score = await _scoringEngine.CalculateScoreAsync(
                token.MintAddress,
                validationResult,
                token.DetectedAt
            );

            // Boost score for optimal bonding progress
            var adjustedScore = Math.Min(100m, score.Score + bondingAnalysis.Confidence * 0.2m);

            // Create trading signal
            var tradingSignal = new SolGemFinder.Shared.Dtos.TradingSignalDto
            {
                Id = Guid.NewGuid(),
                MintAddress = token.MintAddress,
                Score = adjustedScore,
                LiquiditySol = bondingAnalysis.CurrentSolBalance,
                Volume24hSol = null, // Not available during bonding
                HasRecentActivity = true, // Bonding = active trading
                DetectedAt = token.DetectedAt,
                PublishedAt = DateTime.UtcNow,
                Source = "WatchlistMonitor.PumpFun",
                Signals = new SolGemFinder.Shared.Dtos.SignalsDto
                {
                    OnChainValidation = validationResult.IsValid ? 100m : 0m,
                    HolderDistribution = score.Signals?.HolderDistribution ?? 0m,
                    LiquidityDepth = bondingAnalysis.BondingProgress,
                    TradingActivity = 100m, // Active bonding
                    Timing = bondingAnalysis.Confidence
                }
            };

            // Publish to TradingWorker
            await _tradingPublisher.PublishTradingSignalDirectAsync(tradingSignal);

            _logger.LogInformation("🚀✅ Pump.fun BUY SIGNAL sent: {Mint} | Score: {Score}/100 | Bonding: {Progress:F1}%",
                token.MintAddress, adjustedScore, bondingAnalysis.BondingProgress);

            // Update token status
            var updated = token with
            {
                Status = WatchlistStatus.SentToTrading,
                LastUpdated = DateTime.UtcNow
            };

            await _cache.UpdateWatchlistTokenAsync(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send Pump.fun trading signal: {Mint}", token.MintAddress);
        }
    }
}

/// <summary>
/// Actions that can be taken for a token in the watchlist.
/// </summary>
internal enum WatchlistAction
{
    /// <summary>
    /// Keep watching (still pre-bonding).
    /// </summary>
    KeepWatching,

    /// <summary>
    /// Bonding completed, move to scoring.
    /// </summary>
    BondingCompleted,

    /// <summary>
    /// Invalid/failed checks, blacklist.
    /// </summary>
    Blacklist,

    /// <summary>
    /// Too old, expire from watchlist.
    /// </summary>
    Expire
}

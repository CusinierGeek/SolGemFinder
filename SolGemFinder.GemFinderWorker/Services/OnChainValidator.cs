using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SolGemFinder.ScoringWorker.Models;
using SolGemFinder.Shared.Services.BitQuery;

namespace SolGemFinder.ScoringWorker.Services;

/// <summary>
/// Service for validating tokens using BitQuery APIs.
/// Performs critical checks: metadata, holder distribution, trading activity, bonding status.
/// </summary>
public class OnChainValidator
{
    #region Fields

    private readonly ILogger<OnChainValidator> _logger;
    private readonly IBitQueryPumpFunService _bitQuery;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="OnChainValidator"/> class.
    /// </summary>
    public OnChainValidator(
        ILogger<OnChainValidator> logger, 
        IConfiguration config,
        IBitQueryPumpFunService bitQuery)
    {
        _logger = logger;
        _bitQuery = bitQuery;
      
        _logger.LogInformation("🔍 OnChainValidator initialized");
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Validates a token using BitQuery APIs and returns validation result.
    /// Checks: metadata, holder distribution, trading activity, bonding status.
    /// </summary>
    public async Task<ValidationResult> ValidateTokenAsync(string mintAddress)
    {
        _logger.LogDebug("🔍 Validating token: {Mint}", mintAddress[..8]);

        var result = new ValidationResult { MintAddress = mintAddress };

        try
        {

            // 1. Get Token Metadata
            await GetTokenMetadataAsync(result, mintAddress);

            // 2. Get Bonding Progress
            await GetBondingProgressAsync(result, mintAddress);

            // 3. Check Graduation Status
            await CheckGraduationStatusAsync(result, mintAddress);

            // 4. Get Holder Distribution
            await GetHolderDistributionAsync(result, mintAddress);

            // 5. Get Recent Trading Activity
            await GetRecentTradingActivityAsync(result, mintAddress);

            // 6. Perform Validation Checks
            PerformValidationChecks(result);

            result.IsValid = result.Errors.Count == 0;

            if (result.IsValid)
            {
                _logger.LogInformation("✅ Token validation PASSED: {Symbol} ({Mint})", 
                    result.TokenSymbol ?? "Unknown", mintAddress[..8]);
            }
            else
            {
                _logger.LogWarning("❌ Token validation FAILED: {Mint} - Errors: {Errors}", 
                    mintAddress[..8], string.Join(", ", result.Errors));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error validating token: {Mint}", mintAddress[..8]);
            result.AddError($"Validation error: {ex.Message}");
        }

        return result;
    }

    #endregion

    #region Private Methods - BitQuery Operations

    /// <summary>
    /// Gets token metadata from BitQuery.
    /// </summary>
    private async Task GetTokenMetadataAsync(ValidationResult result, string mintAddress)
    {
        try
        {
            _logger.LogDebug("  📊 Getting token metadata...");

            var metadata = await _bitQuery.GetTokenMetadataAsync(mintAddress);

            if (metadata != null)
            {
                result.TokenName = metadata.Name;
                result.TokenSymbol = metadata.Symbol;
                result.MetadataUri = metadata.Uri;
                result.UpdateAuthority = metadata.UpdateAuthority;
                result.IsMutable = metadata.IsMutable;

                _logger.LogInformation("  ✅ Metadata: {Name} ({Symbol}) | Mutable: {Mutable}", 
                    metadata.Name ?? "N/A", 
                    metadata.Symbol ?? "N/A", 
                    metadata.IsMutable);

                // Check if metadata is mutable (security risk)
                if (metadata.IsMutable)
                {
                    result.AddWarning("Token metadata is mutable (can be changed)");
                }
            }
            else
            {
                _logger.LogWarning("  ⚠️ No metadata found");
                result.AddWarning("Token metadata not found (very new or low activity)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting token metadata");
            result.AddWarning("Could not retrieve token metadata");
        }
    }

    /// <summary>
    /// Gets bonding curve progress from BitQuery.
    /// </summary>
    private async Task GetBondingProgressAsync(ValidationResult result, string mintAddress)
    {
        try
        {
            _logger.LogDebug("  📊 Getting bonding progress...");

            var bondingProgress = await _bitQuery.GetBondingProgressAsync(mintAddress);

            if (bondingProgress.HasValue)
            {
                result.BondingProgress = bondingProgress.Value;
                result.IsPumpFunToken = true;

                _logger.LogInformation("  ✅ Bonding Progress: {Progress:F2}%", bondingProgress.Value);

                // Check if ready for graduation (95%+)
                if (bondingProgress.Value >= 95m)
                {
                    _logger.LogInformation("  🎯 Token near graduation (95%+)!");
                }
            }
            else
            {
                _logger.LogDebug("  ⏭️ No bonding data (not a Pump.fun token or already graduated)");
                result.IsPumpFunToken = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting bonding progress");
        }
    }

    /// <summary>
    /// Checks if token has graduated from Pump.fun.
    /// </summary>
    private async Task CheckGraduationStatusAsync(ValidationResult result, string mintAddress)
    {
        try
        {
            _logger.LogDebug("  📊 Checking graduation status...");

            var hasGraduated = await _bitQuery.HasGraduatedAsync(mintAddress);

            result.IsBondingCompleted = hasGraduated;

            if (hasGraduated)
            {
                result.PoolType = "Raydium/Orca";
                _logger.LogInformation("  ✅ Token has GRADUATED to Raydium/Orca - READY TO TRADE!");
            }
            else
            {
                if (result.IsPumpFunToken)
                {
                    _logger.LogDebug("  ⏳ Token still in bonding phase");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking graduation status");
        }
    }

    /// <summary>
    /// Gets holder distribution from BitQuery.
    /// </summary>
    private async Task GetHolderDistributionAsync(ValidationResult result, string mintAddress)
    {
        try
        {
            _logger.LogDebug("  📊 Getting holder distribution...");

            var holders = await _bitQuery.GetTopHoldersAsync(mintAddress, limit: 10);

            result.HolderCount = holders.Count;

            if (holders.Any())
            {
                var totalBalance = holders.Sum(h => h.Balance);
                var topHolderBalance = holders[0].Balance;
                var topHolderPercent = totalBalance > 0 ? (topHolderBalance / totalBalance) * 100 : 0;

                result.TopHoldersPercentage = topHolderPercent;

                _logger.LogInformation("  ✅ Holders: {Count} | Top holder: {Percent:F1}%", 
                    holders.Count, topHolderPercent);

                // Warning if top holder has too much
                if (topHolderPercent > 50)
                {
                    result.AddWarning($"Top holder controls {topHolderPercent:F1}% (whale risk)");
                    _logger.LogWarning("  ⚠️ High concentration: Top holder has {Percent:F1}%", topHolderPercent);
                }
                else if (topHolderPercent > 35)
                {
                    result.AddWarning($"Top holder controls {topHolderPercent:F1}%");
                }
            }
            else
            {
                _logger.LogDebug("  ⏭️ No holder data available");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting holder distribution");
        }
    }

    /// <summary>
    /// Gets recent trading activity from BitQuery.
    /// </summary>
    private async Task GetRecentTradingActivityAsync(ValidationResult result, string mintAddress)
    {
        try
        {
            _logger.LogDebug("  📊 Getting recent trading activity...");

            var trades = await _bitQuery.GetRecentTradesAsync(mintAddress, limit: 20);

            if (trades.Any())
            {
                result.RecentTradeCount = trades.Count;

                // Check activity in last 15 minutes
                var recentTrades = trades.Where(t => t.Time >= DateTime.UtcNow.AddMinutes(-15)).ToList();
                result.HasRecentActivity = recentTrades.Any();

                // Calculate 24h volume
                var volume24h = trades
                    .Where(t => t.Time >= DateTime.UtcNow.AddDays(-1))
                    .Sum(t => t.AmountInUSD);

                result.Volume24hUSD = volume24h;

                _logger.LogInformation("  ✅ Trades (20 recent): {Count} | 24h Volume: ${Volume:F2} | Recent (15min): {Recent}", 
                    trades.Count, volume24h, recentTrades.Count);

                if (!result.HasRecentActivity)
                {
                    result.AddWarning("No trading activity in last 15 minutes");
                }
            }
            else
            {
                _logger.LogWarning("  ⚠️ No recent trades found");
                result.AddWarning("No recent trading activity detected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent trading activity");
        }
    }

    #endregion

    #region Private Methods - Validation Logic

    /// <summary>
    /// Performs final validation checks based on collected data.
    /// </summary>
    private void PerformValidationChecks(ValidationResult result)
    {
        // Check 1: Must have some basic data
        if (string.IsNullOrEmpty(result.TokenName) && string.IsNullOrEmpty(result.TokenSymbol))
        {
            result.AddError("Token has no metadata (name/symbol)");
            _logger.LogWarning("  ❌ No token metadata");
        }

        // Check 2: For Pump.fun tokens, check bonding status
        if (result.IsPumpFunToken)
        {
            if (!result.BondingProgress.HasValue)
            {
                result.AddError("Pump.fun token but no bonding data");
                _logger.LogWarning("  ❌ No bonding progress data");
            }
            else if (result.BondingProgress.Value < 80m && !result.IsBondingCompleted)
            {
                result.AddWarning($"Bonding progress low: {result.BondingProgress.Value:F1}% (early stage)");
                _logger.LogDebug("  ⏳ Early stage token: {Progress:F1}%", result.BondingProgress.Value);
            }
        }

        // Check 3: Holder concentration (already checked above, but log summary)
        if (result.TopHoldersPercentage > 50)
        {
            _logger.LogWarning("  ⚠️ Concentrated holdings: {Percent:F1}%", result.TopHoldersPercentage);
        }

        // Check 4: Trading activity
        if (result.RecentTradeCount == 0)
        {
            result.AddError("No recent trading activity");
            _logger.LogWarning("  ❌ No recent trades");
        }

        // Check 5: Volume check (optional warning)
        if (result.Volume24hUSD.HasValue && result.Volume24hUSD.Value < 1000)
        {
            result.AddWarning($"Low 24h volume: ${result.Volume24hUSD.Value:F2}");
            _logger.LogDebug("  ⚠️ Low volume: ${Volume:F2}", result.Volume24hUSD.Value);
        }
    }

    #endregion
}

#region Data Models

/// <summary>
/// Validation result for a token.
/// </summary>
public class ValidationResult
{
    public required string MintAddress { get; init; }
    public bool IsValid { get; set; }
    
    // Metadata
    public string? TokenName { get; set; }
    public string? TokenSymbol { get; set; }
    public string? MetadataUri { get; set; }
    public string? UpdateAuthority { get; set; }
    public bool IsMutable { get; set; }
    
    // Bonding
    public bool IsPumpFunToken { get; set; }
    public decimal? BondingProgress { get; set; }
    public bool IsBondingCompleted { get; set; }
    public string? PoolType { get; set; }
    
    // Holders
    public int HolderCount { get; set; }
    public decimal TopHoldersPercentage { get; set; }
    
    // Trading
    public int RecentTradeCount { get; set; }
    public bool HasRecentActivity { get; set; }
    public decimal? Volume24hUSD { get; set; }
    
    // Validation
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public void AddError(string error) => Errors.Add(error);
    public void AddWarning(string warning) => Warnings.Add(warning);
}


#endregion

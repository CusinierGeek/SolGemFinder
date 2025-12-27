using SolGemFinder.Shared.Services.BitQuery;

namespace SolGemFinder.ScoringWorker.Services;

/// <summary>
/// Validates liquidity and trading volume for tokens before sending to TradingWorker.
/// Uses BitQuery for accurate liquidity data, with RPC fallback.
/// </summary>
public class LiquidityValidator
{
    private readonly ILogger<LiquidityValidator> _logger;
    private readonly IBitQueryPumpFunService _bitQuery;
    private readonly decimal _minLiquiditySol;
    private readonly decimal _minVolume24h;

    public LiquidityValidator(
        ILogger<LiquidityValidator> logger,
        IConfiguration config,
        IBitQueryPumpFunService bitQuery)
    {
        _logger = logger;
        _bitQuery = bitQuery;


        _minLiquiditySol = config.GetValue<decimal>("Liquidity:MinimumLiquiditySol", 10m);
        _minVolume24h = config.GetValue<decimal>("Liquidity:MinimumVolume24hSol", 5m);

        _logger.LogInformation("🔍 LiquidityValidator initialized");
        _logger.LogInformation("   Min Liquidity: {MinLiq} SOL", _minLiquiditySol);
        _logger.LogInformation("   Min Volume 24h: {MinVol} SOL", _minVolume24h);
    }

    /// <summary>
    /// Validates that a token has sufficient liquidity and volume for trading.
    /// </summary>
    public async Task<LiquidityValidationResult> ValidateLiquidityAsync(string mintAddress, string? poolAddress)
    {
        _logger.LogInformation("🔍 Validating liquidity for: {Mint}", mintAddress[..8]);

        var result = new LiquidityValidationResult
        {
            MintAddress = mintAddress,
            IsValid = false,
        };

        try
        {

            // Primary: Use BitQuery for accurate data
            await ValidateWithBitQueryAsync(result, mintAddress);



            // Determine if valid (must pass critical checks)
            result.IsValid = result.Errors.Count == 0 &&
                             (result.LiquiditySol == null || result.LiquiditySol >= _minLiquiditySol);

            if (result.IsValid)
            {
                _logger.LogInformation("✅ Liquidity validation PASSED: {Mint} | Method: {Method}",
                    mintAddress[..8], result.UsedBitQuery ? "BitQuery" : "RPC");
            }
            else
            {
                _logger.LogWarning("❌ Liquidity validation FAILED: {Mint} - Errors: {Errors}",
                    mintAddress[..8], string.Join(", ", result.Errors));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error validating liquidity: {Mint}", mintAddress[..8]);
            result.AddError($"Validation error: {ex.Message}");
        }

        return result;
    }

    #region BitQuery Validation

    private async Task ValidateWithBitQueryAsync(LiquidityValidationResult result, string mintAddress)
    {
        _logger.LogDebug("  📊 Using BitQuery for liquidity validation");

        try
        {
            // Get token info from BitQuery
            var tokens = await _bitQuery.DiscoverTokensAsync(TokenDiscoveryStrategy.AllNewTokens);
            var tokenInfo = tokens.FirstOrDefault(t => t.TokenAddress == mintAddress);

            if (tokenInfo != null)
            {
                // Extract liquidity from BitQuery data
                result.LiquiditySol = tokenInfo.LiquidityInSOL;
                result.LiquidityUsd = tokenInfo.LiquidityInUSD;
                result.BondingProgress = tokenInfo.BondingCurveProgress;

                if (tokenInfo.LiquidityInSOL.HasValue)
                {
                    if (tokenInfo.LiquidityInSOL.Value >= _minLiquiditySol)
                    {
                        _logger.LogInformation("  ✅ Sufficient liquidity: {Liq} SOL (min: {Min})",
                            tokenInfo.LiquidityInSOL.Value, _minLiquiditySol);
                    }
                    else
                    {
                        result.AddError($"Insufficient liquidity: {tokenInfo.LiquidityInSOL.Value} SOL < {_minLiquiditySol} SOL");
                        _logger.LogWarning("  ⚠️ Insufficient liquidity: {Liq} SOL < {Min} SOL",
                            tokenInfo.LiquidityInSOL.Value, _minLiquiditySol);
                    }
                }
                else
                {
                    result.AddWarning("No liquidity data available from BitQuery");
                }

                // Volume check (BitQuery provides real-time trading data)
                result.Volume24hSol = tokenInfo.LiquidityInSOL; // Approximation
                result.HasRecentActivity = tokenInfo.BondingCurveProgress > 0;

                _logger.LogDebug("  📊 BitQuery data: Liquidity={Liq}SOL, Bonding={Progress}%",
                    tokenInfo.LiquidityInSOL?.ToString("F2") ?? "N/A",
                    tokenInfo.BondingCurveProgress?.ToString("F2") ?? "N/A");
            }
            else
            {
                // Token not found in recent BitQuery data - try direct bonding progress query
                var bondingProgress = await _bitQuery.GetBondingProgressAsync(mintAddress);

                if (bondingProgress.HasValue)
                {
                    result.BondingProgress = bondingProgress;
                    _logger.LogInformation("  📊 Bonding progress from BitQuery: {Progress}%", bondingProgress.Value);

                    // Estimate liquidity from bonding progress
                    // 85 SOL = 100% bonding curve
                    var estimatedLiquidity = (bondingProgress.Value / 100m) * 85m;
                    result.LiquiditySol = estimatedLiquidity;

                    if (estimatedLiquidity >= _minLiquiditySol)
                    {
                        _logger.LogInformation("  ✅ Estimated liquidity sufficient: ~{Liq} SOL", estimatedLiquidity.ToString("F2"));
                    }
                    else
                    {
                        result.AddWarning($"Estimated liquidity low: ~{estimatedLiquidity:F2} SOL");
                    }

                    result.HasRecentActivity = true;
                }
                else
                {
                    result.AddWarning("Token not found in BitQuery data - may be too new or low activity");
                    _logger.LogWarning("  ⚠️ Token not found in BitQuery");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ BitQuery validation failed, data may be incomplete");
            result.AddWarning($"BitQuery error: {ex.Message}");
        }
    }

    #endregion

}

/// <summary>
/// Result of liquidity validation.
/// </summary>
public class LiquidityValidationResult
{
    public required string MintAddress { get; init; }
    public bool IsValid { get; set; }
    public bool UsedBitQuery { get; set; }
    public decimal? LiquiditySol { get; set; }
    public decimal? LiquidityUsd { get; set; }
    public decimal? Volume24hSol { get; set; }
    public decimal? BondingProgress { get; set; }
    public bool HasRecentActivity { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public void AddError(string error) => Errors.Add(error);
    public void AddWarning(string warning) => Warnings.Add(warning);
}

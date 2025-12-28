namespace SolGemFinder.Shared.Dtos;

/// <summary>
/// Token discovery message sent from IndexerWorker to ScoringWorker via ServiceBus.
/// Contains all discovered token data from BitQuery for validation and scoring.
/// </summary>
public record TokenDiscoveryDto
{
    /// <summary>
    /// Unique identifier for this discovery event.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Source of the discovery (e.g., "bitquery-pumpfun").
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Discovery timestamp.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Discovery strategy used (e.g., "AboutToGraduate", "HighPriceMomentum").
    /// </summary>
    public string? Strategy { get; init; }

    /// <summary>
    /// Token mint address.
    /// </summary>
    public required string TokenAddress { get; init; }

    /// <summary>
    /// Transaction signature where token was created.
    /// </summary>
    public required string TransactionSignature { get; init; }

    /// <summary>
    /// Blockchain slot number.
    /// </summary>
    public required ulong Slot { get; init; }

    /// <summary>
    /// Token name from metadata.
    /// </summary>
    public string? TokenName { get; init; }

    /// <summary>
    /// Token symbol from metadata.
    /// </summary>
    public string? TokenSymbol { get; init; }

    /// <summary>
    /// Token creator address.
    /// </summary>
    public string? Creator { get; init; }

    /// <summary>
    /// Bonding curve address (for PumpFun tokens).
    /// </summary>
    public string? BondingCurveAddress { get; init; }

    /// <summary>
    /// Bonding curve progress percentage (0-100).
    /// </summary>
    public decimal? BondingCurveProgress { get; init; }

    /// <summary>
    /// Whether the token has graduated from bonding curve.
    /// </summary>
    public bool IsPostBonding { get; init; }

    /// <summary>
    /// Current liquidity in SOL.
    /// </summary>
    public decimal? LiquidityInSOL { get; init; }

    /// <summary>
    /// Current liquidity in USD.
    /// </summary>
    public decimal? LiquidityInUSD { get; init; }

    /// <summary>
    /// Initial token supply.
    /// </summary>
    public long? InitialSupply { get; init; }

    /// <summary>
    /// Quality score based on discovery strategy and metrics (0-100).
    /// </summary>
    public int QualityScore => CalculateQualityScore();

    /// <summary>
    /// Whether token is near graduation (>95% bonding).
    /// </summary>
    public bool IsNearGraduation => BondingCurveProgress >= 95m;

    /// <summary>
    /// Pump.fun URL for the token.
    /// </summary>
    public string PumpFunUrl => $"https://pump.fun/{TokenAddress}";

    /// <summary>
    /// Solscan URL for the token.
    /// </summary>
    public string SolscanUrl => $"https://solscan.io/token/{TokenAddress}";

    /// <summary>
    /// Calculates quality score based on available metrics.
    /// </summary>
    private int CalculateQualityScore()
    {
        int score = 50; // Base score

        // Bonding progress bonus
        if (BondingCurveProgress.HasValue)
        {
            if (BondingCurveProgress >= 95m)
                score += 30; // Near graduation - highest quality
            else if (BondingCurveProgress >= 85m)
                score += 20;
            else if (BondingCurveProgress >= 70m)
                score += 10;
        }
        else if (IsPostBonding)
        {
            score += 25; // Already graduated
        }

        // Liquidity bonus
        if (LiquidityInSOL.HasValue)
        {
            if (LiquidityInSOL >= 50m)
                score += 15;
            else if (LiquidityInSOL >= 20m)
                score += 10;
            else if (LiquidityInSOL >= 10m)
                score += 5;
        }

        // Metadata completeness bonus
        if (!string.IsNullOrEmpty(TokenName) && !string.IsNullOrEmpty(TokenSymbol))
            score += 5;

        return Math.Min(score, 100); // Cap at 100
    }
}

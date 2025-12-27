namespace SolGemFinder.Shared.Dtos;

/// <summary>
/// Trading signal sent to TradingWorker after scoring and liquidity validation.
/// </summary>
public class TradingSignalDto
{
    /// <summary>
    /// Unique identifier for this signal.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Token mint address.
    /// </summary>
    public required string MintAddress { get; set; }

    /// <summary>
    /// Final score (0-100).
    /// </summary>
    public decimal Score { get; set; }

    /// <summary>
    /// Pool liquidity in SOL.
    /// </summary>
    public decimal? LiquiditySol { get; set; }

    /// <summary>
    /// 24h trading volume in SOL.
    /// </summary>
    public decimal? Volume24hSol { get; set; }

    /// <summary>
    /// Indicates if there was recent trading activity (last 15 min).
    /// </summary>
    public bool HasRecentActivity { get; set; }

    /// <summary>
    /// When the token was first detected.
    /// </summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    /// When this signal was published.
    /// </summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>
    /// Source of the signal (should be "ScoringWorker").
    /// </summary>
    public string Source { get; set; } = "ScoringWorker";

    /// <summary>
    /// Detailed scoring signals breakdown.
    /// </summary>
    public SignalsDto? Signals { get; set; }
}

/// <summary>
/// Detailed scoring signals breakdown.
/// </summary>
public class SignalsDto
{
    public decimal OnChainValidation { get; set; }
    public decimal HolderDistribution { get; set; }
    public decimal LiquidityDepth { get; set; }
    public decimal TradingActivity { get; set; }
    public decimal Timing { get; set; }
}

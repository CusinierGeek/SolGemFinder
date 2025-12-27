namespace SolGemFinder.ScoringWorker.Models;

/// <summary>
/// Represents the scoring result for a token after all checks.
/// </summary>
public record TokenScore
{
    /// <summary>
    /// Token mint address.
    /// </summary>
    public required string MintAddress { get; init; }

    /// <summary>
    /// Overall score (0-100).
    /// </summary>
    public decimal Score { get; init; }

    /// <summary>
    /// Whether the token passes the minimum threshold for trading.
    /// </summary>
    public bool IsTradeableScore => Score >= MinimumTradeableScore;

    /// <summary>
    /// Minimum score required for trading (default: 70).
    /// </summary>
    public const decimal MinimumTradeableScore = 70m;

    /// <summary>
    /// Individual signal scores.
    /// </summary>
    public ScoreSignals Signals { get; init; } = new();

    /// <summary>
    /// Reasons for rejection (if score too low).
    /// </summary>
    public List<string> RejectionReasons { get; init; } = new();

    /// <summary>
    /// Timestamp when the score was calculated.
    /// </summary>
    public DateTime ScoredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the token is recommended for trading.
    /// </summary>
    public bool IsRecommended => IsTradeableScore && Signals.IsHealthy;
}

/// <summary>
/// Individual scoring signals for a token.
/// </summary>
public record ScoreSignals
{
    /// <summary>
    /// Score for on-chain validation (0-30 points).
    /// Mint authority revoked, freeze authority revoked, valid supply.
    /// </summary>
    public decimal OnChainValidation { get; init; }

    /// <summary>
    /// Score for holder distribution (0-25 points).
    /// Top 5 holders < 35%, no whale domination.
    /// </summary>
    public decimal HolderDistribution { get; init; }

    /// <summary>
    /// Score for liquidity depth (0-20 points).
    /// Pool size, slippage, depth.
    /// </summary>
    public decimal LiquidityDepth { get; init; }

    /// <summary>
    /// Score for trading activity (0-15 points).
    /// Volume, new holders arriving, no massive dumps.
    /// </summary>
    public decimal TradingActivity { get; init; }

    /// <summary>
    /// Score for timing (0-10 points).
    /// How quickly after bonding, momentum.
    /// </summary>
    public decimal Timing { get; init; }

    /// <summary>
    /// Whether all critical signals are green.
    /// </summary>
    public bool IsHealthy =>
        OnChainValidation >= 25m &&  // Must pass basic checks
        HolderDistribution >= 15m &&  // Reasonable distribution
        LiquidityDepth >= 10m;       // Minimum liquidity

    /// <summary>
    /// Total score across all signals.
    /// </summary>
    public decimal Total => OnChainValidation + HolderDistribution + LiquidityDepth + TradingActivity + Timing;
}

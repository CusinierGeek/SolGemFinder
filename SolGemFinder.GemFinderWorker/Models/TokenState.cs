namespace SolGemFinder.ScoringWorker.Models;

/// <summary>
/// Represents the complete on-chain state of a token.
/// This is cached to avoid repeated RPC calls.
/// </summary>
public record TokenState
{
    /// <summary>
    /// Token mint address.
    /// </summary>
    public required string MintAddress { get; init; }

    /// <summary>
    /// Whether the token has completed bonding and migrated to AMM.
    /// </summary>
    public bool IsBondingCompleted { get; init; }

    /// <summary>
    /// Whether mint authority has been revoked (anti-rug check).
    /// </summary>
    public bool? IsMintAuthorityRevoked { get; init; }

    /// <summary>
    /// Whether freeze authority has been revoked (anti-freeze check).
    /// </summary>
    public bool? IsFreezeAuthorityRevoked { get; init; }

    /// <summary>
    /// Total supply of the token.
    /// </summary>
    public ulong? TotalSupply { get; init; }

    /// <summary>
    /// Number of decimals for the token.
    /// </summary>
    public byte? Decimals { get; init; }

    /// <summary>
    /// AMM pool address (if bonding completed).
    /// </summary>
    public string? PoolAddress { get; init; }

    /// <summary>
    /// Type of AMM pool (PumpSwap, Raydium, etc.).
    /// </summary>
    public string? PoolType { get; init; }

    /// <summary>
    /// Liquidity amount in the pool (in SOL).
    /// </summary>
    public decimal? LiquidityInSol { get; init; }

    /// <summary>
    /// Number of unique holders.
    /// </summary>
    public int? HolderCount { get; init; }

    /// <summary>
    /// Distribution of top holders (percentages).
    /// </summary>
    public Dictionary<string, decimal>? TopHolders { get; init; }

    /// <summary>
    /// Last time this state was updated.
    /// </summary>
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the token is valid for trading (passed all on-chain checks).
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Validation errors (if any).
    /// </summary>
    public List<string>? ValidationErrors { get; init; }
}

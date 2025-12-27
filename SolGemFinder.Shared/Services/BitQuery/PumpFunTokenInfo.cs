namespace SolGemFinder.Shared.Services.BitQuery;

/// <summary>
/// Represents PumpFun token information from BitQuery or blockchain scanning.
/// </summary>
public record PumpFunTokenInfo
{
    public required string TokenAddress { get; init; }
    public required string TransactionSignature { get; init; }
    public required ulong Slot { get; init; }
    public required DateTime Timestamp { get; init; }
    
    // Bonding status
    public bool IsPostBonding { get; init; }
    
    // Bonding curve progress percentage (0-100)
    public decimal? BondingCurveProgress { get; init; }
    
    // Metadata
    public string? TokenName { get; init; }
    public string? TokenSymbol { get; init; }
    public string? Creator { get; init; }
    
    // Bonding curve address
    public string? BondingCurveAddress { get; init; }
    
    // Initial metrics
    public decimal? InitialLiquidity { get; init; }
    public long? InitialSupply { get; init; }
    
    // Pool metrics from BitQuery
    public decimal? LiquidityInSOL { get; init; }
    public decimal? LiquidityInUSD { get; init; }
}

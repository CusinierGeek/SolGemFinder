namespace SolGemFinder.IndexerWorker.Models;

public record PumpFunTokenInfo
{
    public required string TokenAddress { get; init; }
    public required string TransactionSignature { get; init; }
    public required ulong Slot { get; init; }
    public required DateTime Timestamp { get; init; }
    
    // Bonding status (will be checked by DetectionWorker)
    public bool IsPostBonding { get; init; }
    
    // Metadata (optional, extracted if available)
    public string? TokenName { get; init; }
    public string? TokenSymbol { get; init; }
    public string? Creator { get; init; }
    
    // Bonding curve address (important for tracking)
    public string? BondingCurveAddress { get; init; }
    
    // Initial metrics (if available from creation tx)
    public decimal? InitialLiquidity { get; init; }
    public long? InitialSupply { get; init; }
}

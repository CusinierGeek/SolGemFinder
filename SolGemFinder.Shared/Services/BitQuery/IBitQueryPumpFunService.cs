namespace SolGemFinder.Shared.Services.BitQuery;

/// <summary>
/// Interface for BitQuery PumpFun token discovery service.
/// Provides targeted GraphQL queries for high-quality token discovery.
/// </summary>
public interface IBitQueryPumpFunService
{
    /// <summary>
    /// Discovers high-quality PumpFun tokens using the specified strategy.
    /// </summary>
    /// <param name="strategy">Discovery strategy to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered tokens with rich metadata.</returns>
    Task<List<PumpFunTokenInfo>> DiscoverTokensAsync(
        TokenDiscoveryStrategy strategy = TokenDiscoveryStrategy.AboutToGraduate,
        CancellationToken ct = default);

    /// <summary>
    /// Gets bonding curve progress for a specific token.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bonding curve progress percentage (0-100), or null if not found.</returns>
    Task<decimal?> GetBondingProgressAsync(string mintAddress, CancellationToken ct = default);

    /// <summary>
    /// Checks if a token has graduated from PumpFun.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if graduated, false otherwise.</returns>
    Task<bool> HasGraduatedAsync(string mintAddress, CancellationToken ct = default);

    /// <summary>
    /// Gets token metadata (name, symbol, authority, etc.).
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Token metadata or null if not found.</returns>
    Task<TokenMetadata?> GetTokenMetadataAsync(string mintAddress, CancellationToken ct = default);

    /// <summary>
    /// Gets top token holders with their balances.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <param name="limit">Number of top holders to return (default: 10).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of top holders with balances.</returns>
    Task<List<TokenHolder>> GetTopHoldersAsync(string mintAddress, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Gets recent trades for a token.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <param name="limit">Number of recent trades (default: 20).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recent trades.</returns>
    Task<List<TokenTrade>> GetRecentTradesAsync(string mintAddress, int limit = 20, CancellationToken ct = default);
}

/// <summary>
/// Token metadata from BitQuery.
/// </summary>
public record TokenMetadata
{
    public required string MintAddress { get; init; }
    public string? Name { get; init; }
    public string? Symbol { get; init; }
    public string? Uri { get; init; }
    public string? UpdateAuthority { get; init; }
    public bool IsMutable { get; init; }
}

/// <summary>
/// Token holder information.
/// </summary>
public record TokenHolder
{
    public required string Address { get; init; }
    public decimal Balance { get; init; }
    public decimal? PercentageOfSupply { get; init; }
}

/// <summary>
/// Token trade information.
/// </summary>
public record TokenTrade
{
    public required DateTime Time { get; init; }
    public required string Signature { get; init; }
    public decimal Amount { get; init; }
    public decimal AmountInUSD { get; init; }
    public decimal PriceInUSD { get; init; }
    public string? DexName { get; init; }
    public string? Side { get; init; }
}

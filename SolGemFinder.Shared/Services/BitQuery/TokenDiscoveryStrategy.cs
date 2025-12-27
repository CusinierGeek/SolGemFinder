namespace SolGemFinder.Shared.Services.BitQuery;

/// <summary>
/// Strategy for token discovery using BitQuery.
/// </summary>
public enum TokenDiscoveryStrategy
{
    /// <summary>
    /// Focus on tokens about to graduate (95%+ bonding curve).
    /// Highest quality, lowest noise.
    /// </summary>
    AboutToGraduate,

    /// <summary>
    /// Recently graduated tokens on DEX.
    /// Already validated by market.
    /// </summary>
    RecentlyGraduated,

    /// <summary>
    /// Tokens with high price momentum (5min window).
    /// Early momentum detection.
    /// </summary>
    HighPriceMomentum,

    /// <summary>
    /// Tokens with high market cap growth (5min window).
    /// Market cap momentum detection.
    /// </summary>
    HighMarketCapGrowth,

    /// <summary>
    /// Top tokens by current market cap.
    /// Established tokens only.
    /// </summary>
    TopByMarketCap,

    /// <summary>
    /// All new token creations.
    /// Higher noise, comprehensive coverage.
    /// </summary>
    AllNewTokens
}

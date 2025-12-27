using SolGemFinder.ScoringWorker.Models;

namespace SolGemFinder.ScoringWorker.Services;

/// <summary>
/// Interface for token caching operations using Redis.
/// Manages watchlist, token state, and blacklist with automatic TTL.
/// </summary>
public interface ITokenCacheService
{
    #region Watchlist Operations

    /// <summary>
    /// Adds a token to the watchlist for monitoring.
    /// </summary>
    /// <param name="token">Token to add to watchlist.</param>
    /// <param name="ttl">Time to live (default: 24 hours).</param>
    Task AddToWatchlistAsync(WatchlistToken token, TimeSpan? ttl = null);

    /// <summary>
    /// Gets a token from the watchlist.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <returns>Watchlist token if found, null otherwise.</returns>
    Task<WatchlistToken?> GetFromWatchlistAsync(string mintAddress);

    /// <summary>
    /// Gets all tokens currently in the watchlist.
    /// </summary>
    /// <returns>List of tokens being watched.</returns>
    Task<List<WatchlistToken>> GetAllWatchlistTokensAsync();

    /// <summary>
    /// Updates a token in the watchlist.
    /// </summary>
    /// <param name="token">Updated token data.</param>
    Task UpdateWatchlistTokenAsync(WatchlistToken token);

    /// <summary>
    /// Removes a token from the watchlist.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    Task RemoveFromWatchlistAsync(string mintAddress);

    /// <summary>
    /// Gets count of tokens in watchlist.
    /// </summary>
    Task<int> GetWatchlistCountAsync();

    #endregion

    #region Token State Operations

    /// <summary>
    /// Caches the on-chain state of a token.
    /// </summary>
    /// <param name="state">Token state to cache.</param>
    /// <param name="ttl">Time to live (default: 5 minutes for fast updates).</param>
    Task CacheTokenStateAsync(TokenState state, TimeSpan? ttl = null);

    /// <summary>
    /// Gets cached token state.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <returns>Cached token state if found, null otherwise.</returns>
    Task<TokenState?> GetCachedTokenStateAsync(string mintAddress);

    /// <summary>
    /// Removes token state from cache.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    Task RemoveTokenStateAsync(string mintAddress);

    #endregion

    #region Blacklist Operations

    /// <summary>
    /// Adds a token to the permanent blacklist.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <param name="reason">Reason for blacklisting.</param>
    /// <param name="ttl">Time to live (default: 7 days).</param>
    Task BlacklistTokenAsync(string mintAddress, string reason, TimeSpan? ttl = null);

    /// <summary>
    /// Checks if a token is blacklisted.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <returns>True if blacklisted, false otherwise.</returns>
    Task<bool> IsBlacklistedAsync(string mintAddress);

    /// <summary>
    /// Gets the blacklist reason for a token.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <returns>Blacklist reason if found, null otherwise.</returns>
    Task<string?> GetBlacklistReasonAsync(string mintAddress);

    /// <summary>
    /// Removes a token from the blacklist.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    Task RemoveFromBlacklistAsync(string mintAddress);

    #endregion

    #region Score Caching

    /// <summary>
    /// Caches a token score.
    /// </summary>
    /// <param name="score">Token score to cache.</param>
    /// <param name="ttl">Time to live (default: 10 minutes).</param>
    Task CacheScoreAsync(TokenScore score, TimeSpan? ttl = null);

    /// <summary>
    /// Gets a cached token score.
    /// </summary>
    /// <param name="mintAddress">Token mint address.</param>
    /// <returns>Cached score if found, null otherwise.</returns>
    Task<TokenScore?> GetCachedScoreAsync(string mintAddress);

    #endregion

    #region Health & Stats

    /// <summary>
    /// Checks if Redis connection is healthy.
    /// </summary>
    Task<bool> IsHealthyAsync();

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    Task<CacheStats> GetStatsAsync();

    #endregion
}

/// <summary>
/// Cache statistics for monitoring.
/// </summary>
public record CacheStats
{
    public int WatchlistCount { get; init; }
    public int BlacklistCount { get; init; }
    public int CachedStatesCount { get; init; }
    public bool IsConnected { get; init; }
    public string? RedisVersion { get; init; }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolGemFinder.ScoringWorker.Models;
using StackExchange.Redis;

namespace SolGemFinder.ScoringWorker.Services;

/// <summary>
/// Redis implementation of token caching service.
/// Provides fast, distributed caching for watchlist, state, and scores with automatic TTL.
/// </summary>
public class RedisCacheService : ITokenCacheService
{
    #region Constants

    private const string WatchlistPrefix = "watchlist:";
    private const string StatePrefix = "state:";
    private const string BlacklistPrefix = "blacklist:";
    private const string ScorePrefix = "score:";
    private const string WatchlistSetKey = "watchlist:all";
    private const string BlacklistSetKey = "blacklist:all";

    private static readonly TimeSpan DefaultWatchlistTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultStateTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultBlacklistTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan DefaultScoreTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    #endregion

    #region Fields

    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ILogger<RedisCacheService> _logger;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCacheService"/> class.
    /// </summary>
    /// <param name="redis">Redis connection multiplexer.</param>
    /// <param name="logger">Logger for diagnostic information.</param>
    public RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _logger = logger;

        // Subscribe to connection events
        _redis.ConnectionFailed += OnConnectionFailed;
        _redis.ConnectionRestored += OnConnectionRestored;
        _redis.ErrorMessage += OnErrorMessage;

        _logger.LogInformation("?? RedisCacheService initialized");
        _logger.LogDebug("   Redis Endpoints: {Endpoints}", string.Join(", ", redis.GetEndPoints()));
        _logger.LogInformation("   Connection Status: {Status}", _redis.IsConnected ? "? Connected" : "? Disconnected");
    }

    #endregion

    #region Connection Event Handlers

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs e)
    {
        _logger.LogError("? REDIS CONNECTION FAILED: {EndPoint} - {FailureType} - {Exception}", 
            e.EndPoint, e.FailureType, e.Exception?.Message);
        
        _logger.LogWarning("?? WARNING: Redis data may be lost during disconnection!");
        _logger.LogWarning("?? Blacklist and Watchlist operations will fail until reconnection");
    }

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        _logger.LogInformation("? REDIS CONNECTION RESTORED: {EndPoint}", e.EndPoint);
        _logger.LogWarning("?? WARNING: Redis may have lost data during disconnection!");
        _logger.LogInformation("   Checking data integrity...");
        
        // Schedule a data integrity check
        Task.Run(async () => await CheckDataIntegrityAsync());
    }

    private void OnErrorMessage(object? sender, RedisErrorEventArgs e)
    {
        _logger.LogError("? REDIS ERROR: {Message}", e.Message);
    }

    /// <summary>
    /// Checks data integrity after reconnection and logs warnings if data was lost.
    /// </summary>
    private async Task CheckDataIntegrityAsync()
    {
        try
        {
            var watchlistCount = await GetWatchlistCountAsync();
            var blacklistCount = (int)await _db.SetLengthAsync(BlacklistSetKey);

            _logger.LogInformation("?? Data integrity check: Watchlist={Watch}, Blacklist={Black}", 
                watchlistCount, blacklistCount);

            if (watchlistCount == 0 && blacklistCount == 0)
            {
                _logger.LogError("? CRITICAL: Redis appears empty after reconnection! Data may have been lost.");
                _logger.LogError("? Check Redis persistence settings and maxmemory-policy!");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Failed to check data integrity");
        }
    }

    #endregion

    #region Watchlist Operations

    /// <inheritdoc/>
    public async Task AddToWatchlistAsync(WatchlistToken token, TimeSpan? ttl = null)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogError("? Redis disconnected! Cannot add to watchlist: {Mint}", token.MintAddress);
            throw new InvalidOperationException("Redis connection lost. Cannot add to watchlist.");
        }

        try
        {
            var key = GetWatchlistKey(token.MintAddress);
            var json = JsonSerializer.Serialize(token, JsonOptions);
            var expiry = ttl ?? DefaultWatchlistTtl;

            await _db.StringSetAsync(key, json, expiry);
            await _db.SetAddAsync(WatchlistSetKey, token.MintAddress);

            _logger.LogDebug("? Added to watchlist: {Mint} (TTL: {Ttl})", token.MintAddress, expiry);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while adding to watchlist: {Mint}", token.MintAddress);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<WatchlistToken?> GetFromWatchlistAsync(string mintAddress)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("?? Redis disconnected! Cannot get from watchlist: {Mint}", mintAddress);
            return null;
        }

        try
        {
            var key = GetWatchlistKey(mintAddress);
            var json = await _db.StringGetAsync(key);

            if (json.IsNullOrEmpty)
            {
                _logger.LogTrace("? Watchlist miss: {Mint}", mintAddress);
                return null;
            }

            var token = JsonSerializer.Deserialize<WatchlistToken>(json!.ToString(), JsonOptions);
            _logger.LogTrace("? Watchlist hit: {Mint}", mintAddress);
            return token;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while getting from watchlist: {Mint}", mintAddress);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<List<WatchlistToken>> GetAllWatchlistTokensAsync()
    {
        if (!_redis.IsConnected)
        {
            _logger.LogError("? Redis disconnected! Cannot get watchlist");
            return new List<WatchlistToken>();
        }

        try
        {
            var mintAddresses = await _db.SetMembersAsync(WatchlistSetKey);
            var tokens = new List<WatchlistToken>();

            foreach (var mint in mintAddresses)
            {
                var token = await GetFromWatchlistAsync(mint.ToString());
                if (token != null)
                {
                    tokens.Add(token);
                }
                else
                {
                    // Clean up stale entry
                    await _db.SetRemoveAsync(WatchlistSetKey, mint);
                }
            }

            _logger.LogDebug("?? Retrieved {Count} watchlist tokens", tokens.Count);
            return tokens;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while getting all watchlist tokens");
            return new List<WatchlistToken>();
        }
    }

    /// <inheritdoc/>
    public async Task UpdateWatchlistTokenAsync(WatchlistToken token)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("?? Redis disconnected! Cannot update watchlist: {Mint}", token.MintAddress);
            throw new InvalidOperationException("Redis connection lost. Cannot update watchlist.");
        }

        try
        {
            var key = GetWatchlistKey(token.MintAddress);
            var json = JsonSerializer.Serialize(token, JsonOptions);
            
            // Get remaining TTL to preserve it
            var ttl = await _db.KeyTimeToLiveAsync(key);
            var expiry = ttl ?? DefaultWatchlistTtl;

            await _db.StringSetAsync(key, json, expiry);
            
            _logger.LogDebug("?? Updated watchlist token: {Mint}", token.MintAddress);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while updating watchlist: {Mint}", token.MintAddress);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task RemoveFromWatchlistAsync(string mintAddress)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("?? Redis disconnected! Cannot remove from watchlist: {Mint}", mintAddress);
            // Don't throw here - removal can be retried later
            return;
        }

        try
        {
            var key = GetWatchlistKey(mintAddress);
            await _db.KeyDeleteAsync(key);
            await _db.SetRemoveAsync(WatchlistSetKey, mintAddress);

            _logger.LogDebug("??? Removed from watchlist: {Mint}", mintAddress);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while removing from watchlist: {Mint}", mintAddress);
            // Don't throw - this is not critical
        }
    }

    /// <inheritdoc/>
    public async Task<int> GetWatchlistCountAsync()
    {
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("?? Redis disconnected! Cannot get watchlist count");
            return 0;
        }

        try
        {
            var count = await _db.SetLengthAsync(WatchlistSetKey);
            return (int)count;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while getting watchlist count");
            return 0;
        }
    }

    #endregion

    #region Token State Operations

    /// <inheritdoc/>
    public async Task CacheTokenStateAsync(TokenState state, TimeSpan? ttl = null)
    {
        var key = GetStateKey(state.MintAddress);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var expiry = ttl ?? DefaultStateTtl;

        await _db.StringSetAsync(key, json, expiry);

        _logger.LogDebug("?? Cached token state: {Mint} (TTL: {Ttl})", state.MintAddress, expiry);
    }

    /// <inheritdoc/>
    public async Task<TokenState?> GetCachedTokenStateAsync(string mintAddress)
    {
        var key = GetStateKey(mintAddress);
        var json = await _db.StringGetAsync(key);

        if (json.IsNullOrEmpty)
        {
            _logger.LogTrace("? State cache miss: {Mint}", mintAddress);
            return null;
        }

        var state = JsonSerializer.Deserialize<TokenState>(json!.ToString(), JsonOptions);
        _logger.LogTrace("? State cache hit: {Mint}", mintAddress);
        return state;
    }

    /// <inheritdoc/>
    public async Task RemoveTokenStateAsync(string mintAddress)
    {
        var key = GetStateKey(mintAddress);
        await _db.KeyDeleteAsync(key);

        _logger.LogDebug("??? Removed token state: {Mint}", mintAddress);
    }

    #endregion

    #region Blacklist Operations

    /// <inheritdoc/>
    public async Task BlacklistTokenAsync(string mintAddress, string reason, TimeSpan? ttl = null)
    {
        var key = GetBlacklistKey(mintAddress);
        var expiry = ttl ?? DefaultBlacklistTtl;

        // Check connection health before critical operation
        if (!_redis.IsConnected)
        {
            _logger.LogError("? Redis disconnected! Cannot blacklist token: {Mint}", mintAddress);
            throw new InvalidOperationException("Redis connection lost. Cannot blacklist token.");
        }

        try
        {
            await _db.StringSetAsync(key, reason, expiry);
            await _db.SetAddAsync(BlacklistSetKey, mintAddress);

            // Verify the operation succeeded
            var verifyExists = await _db.KeyExistsAsync(key);
            var verifyInSet = await _db.SetContainsAsync(BlacklistSetKey, mintAddress);
            
            if (!verifyExists || !verifyInSet)
            {
                _logger.LogError("? Blacklist verification FAILED: {Mint} - Exists: {Exists}, InSet: {InSet}", 
                    mintAddress, verifyExists, verifyInSet);
                throw new InvalidOperationException($"Failed to blacklist token {mintAddress}");
            }

            _logger.LogWarning("?? Blacklisted token: {Mint} - Reason: {Reason} (TTL: {Ttl})", 
                mintAddress, reason, expiry);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while blacklisting: {Mint}", mintAddress);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Unexpected error while blacklisting: {Mint}", mintAddress);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsBlacklistedAsync(string mintAddress)
    {
        // Check connection health
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("?? Redis disconnected! Cannot check blacklist for: {Mint}", mintAddress);
            // Return false to avoid blocking the pipeline, but log the issue
            return false;
        }

        try
        {
            var key = GetBlacklistKey(mintAddress);
            var exists = await _db.KeyExistsAsync(key);

            if (exists)
            {
                _logger.LogTrace("?? Token is blacklisted: {Mint}", mintAddress);
            }
            else
            {
                // Clean up stale entry from set if key expired
                await _db.SetRemoveAsync(BlacklistSetKey, mintAddress);
            }

            return exists;
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while checking blacklist: {Mint}", mintAddress);
            return false; // Fail-open to avoid blocking
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetBlacklistReasonAsync(string mintAddress)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("?? Redis disconnected! Cannot get blacklist reason for: {Mint}", mintAddress);
            return null;
        }

        try
        {
            var key = GetBlacklistKey(mintAddress);
            var reason = await _db.StringGetAsync(key);

            return reason.IsNullOrEmpty ? null : reason.ToString();
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while getting blacklist reason: {Mint}", mintAddress);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task RemoveFromBlacklistAsync(string mintAddress)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogError("? Redis disconnected! Cannot remove from blacklist: {Mint}", mintAddress);
            throw new InvalidOperationException("Redis connection lost. Cannot remove from blacklist.");
        }

        try
        {
            var key = GetBlacklistKey(mintAddress);
            await _db.KeyDeleteAsync(key);
            await _db.SetRemoveAsync(BlacklistSetKey, mintAddress);

            _logger.LogInformation("? Removed from blacklist: {Mint}", mintAddress);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogError(ex, "? Redis connection error while removing from blacklist: {Mint}", mintAddress);
            throw;
        }
    }

    #endregion

    #region Score Caching

    /// <inheritdoc/>
    public async Task CacheScoreAsync(TokenScore score, TimeSpan? ttl = null)
    {
        var key = GetScoreKey(score.MintAddress);
        var json = JsonSerializer.Serialize(score, JsonOptions);
        var expiry = ttl ?? DefaultScoreTtl;

        await _db.StringSetAsync(key, json, expiry);

        _logger.LogDebug("?? Cached score: {Mint} = {Score} (TTL: {Ttl})", 
            score.MintAddress, score.Score, expiry);
    }

    /// <inheritdoc/>
    public async Task<TokenScore?> GetCachedScoreAsync(string mintAddress)
    {
        var key = GetScoreKey(mintAddress);
        var json = await _db.StringGetAsync(key);

        if (json.IsNullOrEmpty)
        {
            _logger.LogTrace("? Score cache miss: {Mint}", mintAddress);
            return null;
        }

        var score = JsonSerializer.Deserialize<TokenScore>(json!.ToString(), JsonOptions);
        _logger.LogTrace("? Score cache hit: {Mint} = {Score}", mintAddress, score?.Score);
        return score;
    }

    #endregion

    #region Health & Stats

    /// <inheritdoc/>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            await _db.PingAsync();
            return _redis.IsConnected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis health check failed");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<CacheStats> GetStatsAsync()
    {
        try
        {
            var watchlistCount = await GetWatchlistCountAsync();
            var blacklistCount = (int)await _db.SetLengthAsync(BlacklistSetKey);
            
            // Avoid using KEYS and INFO commands that require admin mode
            // Instead, use simple counts from existing data structures
            
            return new CacheStats
            {
                WatchlistCount = watchlistCount,
                BlacklistCount = blacklistCount,
                CachedStatesCount = 0, // Placeholder - would need separate tracking
                IsConnected = _redis.IsConnected,
                RedisVersion = "Unknown (admin mode disabled)"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cache stats");
            return new CacheStats { IsConnected = false };
        }
    }

    #endregion

    #region Helper Methods

    private static string GetWatchlistKey(string mintAddress) => $"{WatchlistPrefix}{mintAddress}";
    private static string GetStateKey(string mintAddress) => $"{StatePrefix}{mintAddress}";
    private static string GetBlacklistKey(string mintAddress) => $"{BlacklistPrefix}{mintAddress}";
    private static string GetScoreKey(string mintAddress) => $"{ScorePrefix}{mintAddress}";

    #endregion
}

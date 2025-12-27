namespace SolGemFinder.ScoringWorker.Models;

/// <summary>
/// Represents a token captured pre-bonding and added to the watchlist for monitoring.
/// </summary>
public record WatchlistToken
{
    /// <summary>
    /// Token mint address (unique identifier).
    /// </summary>
    public required string MintAddress { get; init; }

    /// <summary>
    /// Transaction signature where the token was first detected.
    /// </summary>
    public required string Signature { get; init; }

    /// <summary>
    /// Blockchain slot number when the token was detected.
    /// </summary>
    public required ulong Slot { get; init; }

    /// <summary>
    /// Timestamp when the token was first detected.
    /// </summary>
    public required DateTime DetectedAt { get; init; }

    /// <summary>
    /// Bonding curve address (if available).
    /// </summary>
    public string? BondingCurveAddress { get; init; }

    /// <summary>
    /// Current status of the token in the watchlist.
    /// </summary>
    public WatchlistStatus Status { get; init; } = WatchlistStatus.Watching;

    /// <summary>
    /// Last time the token was updated in the watchlist.
    /// </summary>
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Number of times this token has been checked.
    /// </summary>
    public int CheckCount { get; init; } = 0;

    /// <summary>
    /// Reason for rejection (if blacklisted).
    /// </summary>
    public string? RejectionReason { get; init; }
}

/// <summary>
/// Status of a token in the watchlist.
/// </summary>
public enum WatchlistStatus
{
    /// <summary>
    /// Token is being actively watched (pre-bonding).
    /// </summary>
    Watching,

    /// <summary>
    /// Token bonding completed, ready for scoring.
    /// </summary>
    BondingCompleted,

    /// <summary>
    /// Token passed scoring, sent to trading worker.
    /// </summary>
    SentToTrading,

    /// <summary>
    /// Token rejected and blacklisted.
    /// </summary>
    Blacklisted,

    /// <summary>
    /// Token expired (timeout without bonding completion).
    /// </summary>
    Expired
}

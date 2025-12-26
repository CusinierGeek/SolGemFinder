namespace SolGemFinder.IndexerWorker.Models;

/// <summary>
/// Configuration options for the SolanaRadar service.
/// </summary>
public class SolanaRadarOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "SolanaRadar";

    /// <summary>
    /// Solana RPC URL. Can be overridden by HELIUS_RPC_URL environment variable.
    /// </summary>
    public string RpcUrl { get; set; } = string.Empty;

    /// <summary>
    /// Pump.fun program ID to monitor for token creation and trades.
    /// </summary>
    public string ProgramId { get; set; } = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";

    /// <summary>
    /// Number of slots to look back from current slot on initial startup.
    /// </summary>
    public int InitialSlotOffset { get; set; } = 100;

    /// <summary>
    /// Maximum number of signatures to fetch per RPC call.
    /// </summary>
    public int SignatureLimit { get; set; } = 50;

    /// <summary>
    /// Maximum number of retry attempts for failed RPC calls.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds between retry attempts. Actual delay is multiplied by attempt number.
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 500;

    /// <summary>
    /// Maximum consecutive JSON parsing errors before logging a warning about RPC instability.
    /// </summary>
    public int MaxConsecutiveJsonErrors { get; set; } = 5;
}

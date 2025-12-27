using Microsoft.Extensions.Options;
using SolGemFinder.IndexerWorker.Models;
using SolGemFinder.Shared.Services.BitQuery;
using Solnet.Rpc;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace SolGemFinder.IndexerWorker.Services;

/// <summary>
/// Service for monitoring Solana blockchain for new Pump.fun token creations and trading activity.
/// Scans transaction signatures and filters for token creation and trade patterns.
/// </summary>
public class SolanaRadar : ISolanaRadar
{
    #region Constants

    private const string TokenProgramId = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
    private const string Token2022ProgramId = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
    private const string SystemProgramId = "11111111111111111111111111111111";
    private const string BondingCurveSuffix = "pump";
    private const int MinimumAccountKeysForToken = 1; // CRITICAL: Pump.fun uses Address Lookup Tables
    private const int SolanaAddressLength = 44;
    private const int DeduplicationCacheCapacity = 10000;
    private const string ApiKeyErrorCode = "-32401";
    private const string ApiKeyQueryParam = "api-key=";

    // Token Program opcodes for mint initialization
    private const byte InitializeMintOpcode = 0;      // spl-token InitializeMint
    private const byte InitializeMint2Opcode = 20;    // spl-token-2022 InitializeMint2

    // Set of supported Token Programs (both legacy and Token-2022)
    private static readonly HashSet<string> TokenPrograms = new()
    {
        TokenProgramId,        // spl-token (legacy)
        Token2022ProgramId     // spl-token-2022 (modern Pump.fun)
    };

    #endregion

    #region Fields

    private readonly ILogger<SolanaRadar> _logger;
    private readonly IRpcClient _rpcClient;
    private readonly SolanaRawRpcClient _rawRpcClient;
    private readonly PublicKey _pumpFunProgramId;
    private readonly SolanaRadarOptions _options;
    private ulong _lastProcessedSlot;
    private bool _hasLoggedApiKeyError;
    private readonly HashSet<string> _seenTokens;
    private int _consecutiveJsonErrors;
    private const int MaxConsecutiveJsonErrors = 5;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="SolanaRadar"/> service.
    /// </summary>
    /// <param name="logger">Logger for diagnostic information.</param>
    /// <param name="options">Configuration options for the radar service.</param>
    /// <param name="httpClientFactory">HTTP client factory for raw RPC calls.</param>
    /// <exception cref="InvalidOperationException">Thrown when RPC URL is not configured.</exception>
    public SolanaRadar(ILogger<SolanaRadar> logger, IOptions<SolanaRadarOptions> options, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _options = options.Value;

        var rpcUrl = GetRpcUrl();
        _rpcClient = ClientFactory.GetClient(rpcUrl);
        _pumpFunProgramId = new PublicKey(_options.ProgramId);
        _lastProcessedSlot = 0;
        _hasLoggedApiKeyError = false;
        _seenTokens = new HashSet<string>(DeduplicationCacheCapacity);
        _consecutiveJsonErrors = 0;

        // Initialize raw RPC client for jsonParsed transactions (ALT support)
        var httpClient = httpClientFactory.CreateClient("SolanaRpc");
        var rawRpcLogger = logger as ILogger<SolanaRawRpcClient> ?? 
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SolanaRawRpcClient>();
        _rawRpcClient = new SolanaRawRpcClient(httpClient, rawRpcLogger, rpcUrl);

        _logger.LogInformation("🛰️ SolanaRadar initialized - ULTRA-FAST mode with jsonParsed support");
        _logger.LogInformation("   Environment: {Env}", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production");
        _logger.LogInformation("   RPC: {RpcUrl}", MaskApiKey(rpcUrl));
        _logger.LogInformation("   Program ID: {ProgramId}", _options.ProgramId);
        _logger.LogInformation("   📊 Configuration:");
        _logger.LogInformation("      • Signature Limit: {Limit}", _options.SignatureLimit);
        _logger.LogInformation("      • Initial Slot Offset: {Offset}", _options.InitialSlotOffset);
        _logger.LogInformation("      • Max Retry Attempts: {Max}", _options.MaxRetryAttempts);
        _logger.LogInformation("      • Retry Delay: {Delay}ms", _options.RetryDelayMilliseconds);
        _logger.LogInformation("      • Max Consecutive JSON Errors: {Max}", _options.MaxConsecutiveJsonErrors);
        _logger.LogInformation("      • ALT Support: ✅ Enabled via jsonParsed encoding");
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Scans for new Pump.fun tokens by monitoring program transactions.
    /// Processes signatures in chronological order and filters for token creations and trades.
    /// </summary>
    /// <param name="ct">Cancellation token for stopping the scan operation.</param>
    /// <returns>A list of newly discovered token information.</returns>
    public async Task<List<PumpFunTokenInfo>> ScanForNewTokensAsync(CancellationToken ct = default)
    {
        var discoveries = new List<PumpFunTokenInfo>();

        try
        {
            var currentSlot = await GetCurrentSlotAsync();
            if (currentSlot == 0)
            {
                return discoveries;
            }

            InitializeLastProcessedSlot(currentSlot);

            if (_lastProcessedSlot >= currentSlot)
            {
                return discoveries;
            }

            _logger.LogDebug("🔍 Scanning slots {LastSlot} → {CurrentSlot}", _lastProcessedSlot, currentSlot);

            var newSignatures = await GetNewSignaturesAsync(currentSlot);
            if (newSignatures.Count == 0)
            {
                _lastProcessedSlot = currentSlot;
                return discoveries;
            }

            _logger.LogInformation("📡 Found {Count} Pump.fun transactions to scan", newSignatures.Count);

            var (processed, filtered) = await ProcessSignaturesAsync(newSignatures, discoveries, ct);

            UpdateLastProcessedSlot(newSignatures, currentSlot);
            LogStatistics(processed, filtered, discoveries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning for new tokens");
        }

        return discoveries;
    }

    #endregion

    #region Configuration and Initialization

    /// <summary>
    /// Gets the RPC URL from configuration or environment variables.
    /// </summary>
    /// <returns>The configured RPC URL.</returns>
    /// <exception cref="InvalidOperationException">Thrown when RPC URL is not configured.</exception>
    private string GetRpcUrl()
    {
        var rpcUrl = _options.RpcUrl;
        if (string.IsNullOrWhiteSpace(rpcUrl))
        {
            rpcUrl = Environment.GetEnvironmentVariable("HELIUS_RPC_URL");
            if (string.IsNullOrWhiteSpace(rpcUrl))
            {
                throw new InvalidOperationException("HELIUS_RPC_URL must be configured in appsettings or environment variable");
            }
        }
        return rpcUrl;
    }

    /// <summary>
    /// Initializes the last processed slot on first run.
    /// </summary>
    /// <param name="currentSlot">The current blockchain slot.</param>
    private void InitializeLastProcessedSlot(ulong currentSlot)
    {
        if (_lastProcessedSlot == 0)
        {
            _lastProcessedSlot = currentSlot - (ulong)_options.InitialSlotOffset;
            _logger.LogInformation("Starting slot scanning from slot {Slot}", _lastProcessedSlot);
        }
    }

    #endregion

    #region Blockchain RPC Operations

    /// <summary>
    /// Gets the current confirmed slot from the Solana blockchain.
    /// </summary>
    /// <returns>The current slot number, or 0 if the request fails.</returns>
    private async Task<ulong> GetCurrentSlotAsync()
    {
        var slotResponse = await _rpcClient.GetSlotAsync(Solnet.Rpc.Types.Commitment.Confirmed);

        if (!slotResponse.WasRequestSuccessfullyHandled)
        {
            if (IsApiKeyError(slotResponse.Reason))
            {
                LogApiKeyError();
                return 0;
            }

            _logger.LogWarning("Failed to get current slot: {Error}", slotResponse.Reason);
            return 0;
        }

        _hasLoggedApiKeyError = false;
        return slotResponse.Result;
    }

    /// <summary>
    /// Gets new signatures for the Pump.fun program since the last processed slot.
    /// Implements retry logic for transient errors like JSON parsing failures.
    /// </summary>
    /// <param name="currentSlot">The current blockchain slot.</param>
    /// <returns>List of new signatures ordered by slot.</returns>
    private async Task<List<SignatureStatusInfo>> GetNewSignaturesAsync(ulong currentSlot)
    {
        for (int attempt = 1; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            try
            {
                var signaturesResponse = await _rpcClient.GetSignaturesForAddressAsync(
                    _pumpFunProgramId.Key,
                    limit: (ulong)_options.SignatureLimit,
                    commitment: Solnet.Rpc.Types.Commitment.Confirmed
                );

                if (!signaturesResponse.WasRequestSuccessfullyHandled || signaturesResponse.Result == null)
                {
                    var isJsonError = signaturesResponse.Reason?.Contains("parse json", StringComparison.OrdinalIgnoreCase) == true;

                    if (isJsonError)
                    {
                        _consecutiveJsonErrors++;

                        if (_consecutiveJsonErrors >= _options.MaxConsecutiveJsonErrors)
                        {
                            _logger.LogWarning("⚠️ Persistent JSON parsing errors detected ({Count}/{Max}). RPC may be unstable.",
                                _consecutiveJsonErrors, _options.MaxConsecutiveJsonErrors);
                        }

                        if (attempt < _options.MaxRetryAttempts)
                        {
                            var delay = _options.RetryDelayMilliseconds * attempt;
                            _logger.LogDebug("🔄 JSON parse error on attempt {Attempt}/{Max}. Retrying in {Delay}ms...",
                                attempt, _options.MaxRetryAttempts, delay);
                            await Task.Delay(delay);
                            continue;
                        }
                    }

                    _logger.LogWarning("Failed to get signatures after {Attempts} attempts: {Error}",
                        attempt, signaturesResponse.Reason);
                    return new List<SignatureStatusInfo>();
                }

                // Success - reset error counter
                if (_consecutiveJsonErrors > 0)
                {
                    _logger.LogInformation("✅ RPC connection recovered after {Errors} errors", _consecutiveJsonErrors);
                    _consecutiveJsonErrors = 0;
                }

                var filteredSignatures = signaturesResponse.Result
                    .Where(s => s.Slot > _lastProcessedSlot && s.Slot <= currentSlot)
                    .OrderBy(s => s.Slot)
                    .ToList();

                return filteredSignatures;
            }
            catch (Exception ex)
            {
                if (attempt < _options.MaxRetryAttempts)
                {
                    var delay = _options.RetryDelayMilliseconds * attempt;
                    _logger.LogWarning(ex, "Exception on attempt {Attempt}/{Max}. Retrying in {Delay}ms...",
                        attempt, _options.MaxRetryAttempts, delay);
                    await Task.Delay(delay);
                }
                else
                {
                    _logger.LogError(ex, "Failed to get signatures after {Attempts} attempts", _options.MaxRetryAttempts);
                    return new List<SignatureStatusInfo>();
                }
            }
        }

        return new List<SignatureStatusInfo>();
    }

    #endregion

    #region Transaction Processing

    /// <summary>
    /// Processes a list of signatures and extracts token information.
    /// </summary>
    /// <param name="signatures">List of signatures to process.</param>
    /// <param name="discoveries">List to add discovered tokens to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple containing the number of processed and filtered transactions.</returns>
    private async Task<(int processed, int filtered)> ProcessSignaturesAsync(
        List<SignatureStatusInfo> signatures,
        List<PumpFunTokenInfo> discoveries,
        CancellationToken ct)
    {
        int processed = 0;
        int filtered = 0;
        var mintsSeen = new HashSet<string>(); // Per-batch deduplication by mint address
        var signaturesProcessed = new HashSet<string>(); // Prevent processing same signature twice

        foreach (var signature in signatures)
        {
            if (ct.IsCancellationRequested)
                break;

            // Skip if we've already processed this signature in this batch
            if (signaturesProcessed.Contains(signature.Signature))
            {
                _logger.LogWarning("🔁 Duplicate signature in batch: {Signature}", signature.Signature[..8]);
                filtered++;
                continue;
            }

            signaturesProcessed.Add(signature.Signature);
            processed++;

            try
            {
                var tokenInfo = await ProcessTransactionAsync(signature, ct);
                if (tokenInfo != null)
                {
                    // Check if already seen globally
                    if (_seenTokens.Contains(tokenInfo.TokenAddress))
                    {
                        _logger.LogDebug("🔁 Token already seen (global): {Mint}", tokenInfo.TokenAddress[..8]);
                        filtered++;
                        continue;
                    }

                    // Check if already seen in this batch (same mint, different bonding curve)
                    if (mintsSeen.Contains(tokenInfo.TokenAddress))
                    {
                        _logger.LogWarning("🔁 Token already seen in batch (duplicate bonding curve): {Mint} | Signature: {Sig}",
                            tokenInfo.TokenAddress[..8], tokenInfo.TransactionSignature[..8]);
                        filtered++;
                        continue;
                    }

                    // Add to discoveries and mark as seen
                    discoveries.Add(tokenInfo);
                    _seenTokens.Add(tokenInfo.TokenAddress);
                    mintsSeen.Add(tokenInfo.TokenAddress);

                    _logger.LogInformation("✨ NEW TOKEN ADDED: {TokenAddress} | Slot: {Slot} | _seenTokens.Count={Count}",
                        tokenInfo.TokenAddress, tokenInfo.Slot, _seenTokens.Count);
                }
                else
                {
                    filtered++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error processing transaction {Signature}", signature.Signature);
                filtered++;
            }
        }

        return (processed, filtered);
    }

    /// <summary>
    /// Processes a single transaction and attempts to extract token information.
    /// Uses jsonParsed encoding to automatically resolve Address Lookup Tables.
    /// </summary>
    /// <param name="signatureInfo">Signature status information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Token information if extraction was successful, null otherwise.</returns>
    private async Task<PumpFunTokenInfo?> ProcessTransactionAsync(SignatureStatusInfo signatureInfo, CancellationToken ct)
    {
        var sigShort = signatureInfo.Signature[..8];
        
        if (signatureInfo.ConfirmationStatus == null)
        {
            _logger.LogDebug("⏭️ Skipping {Sig}: No confirmation status", sigShort);
            return null;
        }

        try
        {
            // CRITICAL: Use jsonParsed encoding to get ALT-resolved accounts
            var parsedDoc = await _rawRpcClient.GetTransactionParsedAsync(signatureInfo.Signature, ct);
            if (parsedDoc == null)
            {
                _logger.LogWarning("⚠️ REJECTED {Sig}: Failed to fetch jsonParsed transaction (RPC error or null response)", sigShort);
                return null;
            }

            // Extract Pump.fun token creation
            var extraction = _rawRpcClient.ExtractPumpFunTokenCreation(parsedDoc, _pumpFunProgramId.Key);
            if (extraction == null)
            {
                _logger.LogDebug("⏭️ Skipping {Sig}: Not a Pump.fun CREATE instruction (could be buy/sell/other)", sigShort);
                return null;
            }

            var (mint, bonding) = extraction.Value;
            if (string.IsNullOrEmpty(mint) || string.IsNullOrEmpty(bonding))
            {
                _logger.LogWarning("⚠️ REJECTED {Sig}: Extracted empty mint ({Mint}) or bonding ({Bonding})", 
                    sigShort, 
                    string.IsNullOrEmpty(mint) ? "EMPTY" : mint[..8],
                    string.IsNullOrEmpty(bonding) ? "EMPTY" : bonding[..8]);
                return null;
            }

            var timestamp = GetTimestamp(signatureInfo);

            _logger.LogInformation("✅ PUMP.FUN TOKEN EXTRACTED: {Mint} | Bonding: {Bonding} | Sig: {Sig}",
                mint, bonding, sigShort);

            return new PumpFunTokenInfo
            {
                TokenAddress = mint,
                TransactionSignature = signatureInfo.Signature,
                Slot = signatureInfo.Slot,
                Timestamp = timestamp,
                IsPostBonding = false,
                BondingCurveAddress = bonding
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ EXCEPTION processing transaction {Sig} (Slot: {Slot})", sigShort, signatureInfo.Slot);
            return null;
        }
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Updates the last processed slot after processing signatures.
    /// </summary>
    /// <param name="signatures">List of processed signatures.</param>
    /// <param name="currentSlot">The current blockchain slot.</param>
    private void UpdateLastProcessedSlot(List<SignatureStatusInfo> signatures, ulong currentSlot)
    {
        if (signatures.Any())
        {
            _lastProcessedSlot = signatures.Max(s => s.Slot);
        }
        else
        {
            _lastProcessedSlot = currentSlot;
        }
    }

    /// <summary>
    /// Gets the timestamp from signature information or uses current UTC time as fallback.
    /// </summary>
    /// <param name="signatureInfo">Signature status information.</param>
    /// <returns>The transaction timestamp.</returns>
    private static DateTime GetTimestamp(SignatureStatusInfo signatureInfo)
    {
        return signatureInfo.BlockTime.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds((long)signatureInfo.BlockTime.Value).DateTime
            : DateTime.UtcNow;
    }

    /// <summary>
    /// Masks the API key in the URL for logging purposes.
    /// </summary>
    /// <param name="url">The URL containing the API key.</param>
    /// <returns>The URL with the API key masked.</returns>
    private static string MaskApiKey(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        var apiKeyIndex = url.IndexOf(ApiKeyQueryParam, StringComparison.OrdinalIgnoreCase);
        if (apiKeyIndex == -1)
            return url;

        var keyStart = apiKeyIndex + ApiKeyQueryParam.Length;
        var keyEnd = url.IndexOf('&', keyStart);
        if (keyEnd == -1)
            keyEnd = url.Length;

        return url[..keyStart] + "***" + url[keyEnd..];
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Checks if the error response indicates an API key issue.
    /// </summary>
    /// <param name="reason">The error reason from the RPC response.</param>
    /// <returns>True if the error is API key related, false otherwise.</returns>
    private static bool IsApiKeyError(string? reason)
    {
        return reason?.Contains(ApiKeyErrorCode) == true ||
               reason?.Contains("invalid api key", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Logs API key error message once to avoid spam.
    /// </summary>
    private void LogApiKeyError()
    {
        if (!_hasLoggedApiKeyError)
        {
            _logger.LogCritical("❌ Invalid Helius API key! Please check your configuration.");
            _logger.LogCritical("   Update HELIUS_RPC_URL with a valid API key from https://helius.dev/");
            _hasLoggedApiKeyError = true;
        }
    }

    #endregion

    #region Logging

    /// <summary>
    /// Logs processing statistics.
    /// </summary>
    /// <param name="processed">Number of transactions processed.</param>
    /// <param name="filtered">Number of transactions filtered out.</param>
    /// <param name="discoveryCount">Number of tokens discovered.</param>
    private void LogStatistics(int processed, int filtered, int discoveryCount)
    {
        if (processed > 0)
        {
            var rejectRate = filtered * 100 / processed;
            _logger.LogInformation("📊 Processed: {Processed} | Kept: {Kept} | Filtered: {Filtered} ({Rate}%)",
                processed, discoveryCount, filtered, rejectRate);
        }
    }

    #endregion
}

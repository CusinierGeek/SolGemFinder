using Microsoft.Extensions.Options;
using SolGemFinder.IndexerWorker.Models;
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
    private const string SystemProgramId = "11111111111111111111111111111111";
    private const string BondingCurveSuffix = "pump";
    private const int MinimumAccountKeysForToken = 8;
    private const int SolanaAddressLength = 44;
    private const int DeduplicationCacheCapacity = 10000;
    private const string ApiKeyErrorCode = "-32401";
    private const string ApiKeyQueryParam = "api-key=";

    #endregion

    #region Fields

    private readonly ILogger<SolanaRadar> _logger;
    private readonly IRpcClient _rpcClient;
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
    /// <exception cref="InvalidOperationException">Thrown when RPC URL is not configured.</exception>
    public SolanaRadar(ILogger<SolanaRadar> logger, IOptions<SolanaRadarOptions> options)
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

        _logger.LogInformation("🛰️ SolanaRadar initialized - ULTRA-FAST mode");
        _logger.LogInformation("   Environment: {Env}", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production");
        _logger.LogInformation("   RPC: {RpcUrl}", MaskApiKey(rpcUrl));
        _logger.LogInformation("   Program ID: {ProgramId}", _options.ProgramId);
        _logger.LogInformation("   📊 Configuration:");
        _logger.LogInformation("      • Signature Limit: {Limit}", _options.SignatureLimit);
        _logger.LogInformation("      • Initial Slot Offset: {Offset}", _options.InitialSlotOffset);
        _logger.LogInformation("      • Max Retry Attempts: {Max}", _options.MaxRetryAttempts);
        _logger.LogInformation("      • Retry Delay: {Delay}ms", _options.RetryDelayMilliseconds);
        _logger.LogInformation("      • Max Consecutive JSON Errors: {Max}", _options.MaxConsecutiveJsonErrors);
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

    /// <summary>
    /// Gets transaction details from the blockchain.
    /// Implements retry logic for transient RPC errors.
    /// </summary>
    /// <param name="signature">Transaction signature.</param>
    /// <returns>Transaction metadata, or null if the request fails.</returns>
    private async Task<TransactionMetaInfo?> GetTransactionAsync(string signature)
    {
        for (int attempt = 1; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            try
            {
                var txResponse = await _rpcClient.GetTransactionAsync(
                    signature,
                    commitment: Solnet.Rpc.Types.Commitment.Confirmed
                );

                if (!txResponse.WasRequestSuccessfullyHandled || txResponse.Result?.Transaction?.Message == null)
                {
                    var isJsonError = txResponse.Reason?.Contains("parse json", StringComparison.OrdinalIgnoreCase) == true;
                    
                    if (isJsonError && attempt < _options.MaxRetryAttempts)
                    {
                        var delay = _options.RetryDelayMilliseconds * attempt;
                        _logger.LogTrace("🔄 JSON parse error getting transaction {Signature}. Retrying in {Delay}ms...",
                            signature[..8], delay);
                        await Task.Delay(delay);
                        continue;
                    }

                    return null;
                }

                return txResponse.Result;
            }
            catch (Exception ex)
            {
                if (attempt < _options.MaxRetryAttempts)
                {
                    var delay = _options.RetryDelayMilliseconds * attempt;
                    _logger.LogTrace(ex, "Exception getting transaction {Signature}. Retrying in {Delay}ms...",
                        signature[..8], delay);
                    await Task.Delay(delay);
                }
                else
                {
                    _logger.LogDebug(ex, "Failed to get transaction {Signature} after {Attempts} attempts",
                        signature[..8], _options.MaxRetryAttempts);
                    return null;
                }
            }
        }

        return null;
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

        foreach (var signature in signatures)
        {
            if (ct.IsCancellationRequested)
                break;

            processed++;

            try
            {
                var tokenInfo = await ProcessTransactionAsync(signature, ct);
                if (tokenInfo != null)
                {
                    if (_seenTokens.Contains(tokenInfo.TokenAddress))
                    {
                        filtered++;
                        continue;
                    }

                    _seenTokens.Add(tokenInfo.TokenAddress);
                    discoveries.Add(tokenInfo);

                    _logger.LogInformation("✨ NEW TOKEN: {TokenAddress} | Slot: {Slot}",
                        tokenInfo.TokenAddress, tokenInfo.Slot);
                }
                else
                {
                    filtered++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transaction {Signature}", signature.Signature);
            }
        }

        return (processed, filtered);
    }

    /// <summary>
    /// Processes a single transaction and attempts to extract token information.
    /// </summary>
    /// <param name="signatureInfo">Signature status information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Token information if extraction was successful, null otherwise.</returns>
    private async Task<PumpFunTokenInfo?> ProcessTransactionAsync(SignatureStatusInfo signatureInfo, CancellationToken ct)
    {
        if (signatureInfo.ConfirmationStatus == null)
        {
            return null;
        }

        try
        {
            var transaction = await GetTransactionAsync(signatureInfo.Signature);
            if (transaction == null)
            {
                return null;
            }

            var message = transaction.Transaction.Message;
            var accountKeys = message.AccountKeys.ToList();

            if (!IsValidTokenTransaction(accountKeys))
            {
                return null;
            }

            var (hasCreateAccount, hasInitializeMint, isPumpFunTrade) = AnalyzeInstructions(message.Instructions, accountKeys);

            // Priority 1: Token creation
            if (hasCreateAccount && hasInitializeMint)
            {
                return await ExtractTokenCreationAsync(accountKeys, signatureInfo, transaction);
            }

            // Priority 2: Pump.fun trade
            if (isPumpFunTrade)
            {
                return await ExtractTokenFromTradeAsync(accountKeys, signatureInfo, transaction);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing transaction {Signature}", signatureInfo.Signature);
            return null;
        }
    }

    #endregion

    #region Transaction Analysis

    /// <summary>
    /// Validates if the transaction has the minimum requirements for a token transaction.
    /// </summary>
    /// <param name="accountKeys">List of account keys in the transaction.</param>
    /// <returns>True if valid, false otherwise.</returns>
    private static bool IsValidTokenTransaction(List<string> accountKeys)
    {
        return accountKeys.Count >= MinimumAccountKeysForToken &&
               accountKeys.Contains(TokenProgramId);
    }

    /// <summary>
    /// Analyzes transaction instructions to determine the transaction type.
    /// </summary>
    /// <param name="instructions">Transaction instructions.</param>
    /// <param name="accountKeys">List of account keys.</param>
    /// <returns>Tuple indicating if the transaction has create account, initialize mint, or is a Pump.fun trade.</returns>
    private (bool hasCreateAccount, bool hasInitializeMint, bool isPumpFunTrade) AnalyzeInstructions(
        InstructionInfo[] instructions,
        List<string> accountKeys)
    {
        bool hasCreateAccount = false;
        bool hasInitializeMint = false;
        bool isPumpFunTrade = false;

        foreach (var instruction in instructions)
        {
            var programId = accountKeys[(int)instruction.ProgramIdIndex];

            if (programId == SystemProgramId && instruction.Data?.Length > 0)
            {
                hasCreateAccount = true;
            }
            else if (programId == TokenProgramId && instruction.Data?.Length > 0)
            {
                hasInitializeMint = true;
            }
            else if (programId == _pumpFunProgramId.Key && instruction.Data?.Length > 0)
            {
                isPumpFunTrade = true;
            }
        }

        return (hasCreateAccount, hasInitializeMint, isPumpFunTrade);
    }

    #endregion

    #region Token Extraction

    /// <summary>
    /// Extracts token information from a token creation transaction.
    /// </summary>
    /// <param name="accountKeys">List of account keys in the transaction.</param>
    /// <param name="signatureInfo">Signature status information.</param>
    /// <param name="transaction">Transaction metadata.</param>
    /// <returns>Token information if extraction was successful, null otherwise.</returns>
    private async Task<PumpFunTokenInfo?> ExtractTokenCreationAsync(
        List<string> accountKeys,
        SignatureStatusInfo signatureInfo,
        TransactionMetaInfo transaction)
    {
        var potentialMintAddress = FindPotentialMintAddress(accountKeys);
        if (string.IsNullOrEmpty(potentialMintAddress))
        {
            return null;
        }

        var bondingCurveAddress = FindBondingCurveAddress(accountKeys);
        var timestamp = GetTimestamp(signatureInfo);

        _logger.LogInformation("🆕 NEW TOKEN CREATED: {Mint} | Bonding: {Bonding}",
            potentialMintAddress, bondingCurveAddress ?? "N/A");

        return new PumpFunTokenInfo
        {
            TokenAddress = potentialMintAddress,
            TransactionSignature = signatureInfo.Signature,
            Slot = signatureInfo.Slot,
            Timestamp = timestamp,
            IsPostBonding = false,
            BondingCurveAddress = bondingCurveAddress
        };
    }

    /// <summary>
    /// Extracts token information from a Pump.fun trade transaction.
    /// </summary>
    /// <param name="accountKeys">List of account keys in the transaction.</param>
    /// <param name="signatureInfo">Signature status information.</param>
    /// <param name="transaction">Transaction metadata.</param>
    /// <returns>Token information if extraction was successful, null otherwise.</returns>
    private async Task<PumpFunTokenInfo?> ExtractTokenFromTradeAsync(
        List<string> accountKeys,
        SignatureStatusInfo signatureInfo,
        TransactionMetaInfo transaction)
    {
        var bondingCurveAddress = FindBondingCurveAddress(accountKeys);
        if (string.IsNullOrEmpty(bondingCurveAddress))
        {
            return null;
        }

        var potentialMintAddress = FindMintAddressFromTrade(accountKeys, bondingCurveAddress);
        if (string.IsNullOrEmpty(potentialMintAddress))
        {
            return null;
        }

        // Avoid duplicates
        if (_seenTokens.Contains(potentialMintAddress))
        {
            return null;
        }

        var timestamp = GetTimestamp(signatureInfo);

        _logger.LogInformation("💰 ACTIVE TOKEN (from trade): {Mint} | Bonding: {Bonding}",
            potentialMintAddress, bondingCurveAddress);

        return new PumpFunTokenInfo
        {
            TokenAddress = potentialMintAddress,
            TransactionSignature = signatureInfo.Signature,
            Slot = signatureInfo.Slot,
            Timestamp = timestamp,
            IsPostBonding = false,
            BondingCurveAddress = bondingCurveAddress
        };
    }

    #endregion

    #region Address Helpers

    /// <summary>
    /// Finds the potential mint address from account keys.
    /// </summary>
    /// <param name="accountKeys">List of account keys.</param>
    /// <returns>The potential mint address, or null if not found.</returns>
    private string? FindPotentialMintAddress(List<string> accountKeys)
    {
        return accountKeys
            .Skip(3)
            .Take(5)
            .FirstOrDefault(k =>
                k != _pumpFunProgramId.Key &&
                !k.StartsWith("Token") &&
                !k.StartsWith("11111") &&
                !k.StartsWith("Sysvar"));
    }

    /// <summary>
    /// Finds the bonding curve address from account keys.
    /// </summary>
    /// <param name="accountKeys">List of account keys.</param>
    /// <returns>The bonding curve address, or null if not found.</returns>
    private static string? FindBondingCurveAddress(List<string> accountKeys)
    {
        return accountKeys
            .FirstOrDefault(k => k.EndsWith(BondingCurveSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the mint address from a trade transaction.
    /// </summary>
    /// <param name="accountKeys">List of account keys.</param>
    /// <param name="bondingCurveAddress">The bonding curve address to exclude.</param>
    /// <returns>The mint address, or null if not found.</returns>
    private string? FindMintAddressFromTrade(List<string> accountKeys, string bondingCurveAddress)
    {
        return accountKeys
            .Where(k =>
                k != bondingCurveAddress &&
                k != _pumpFunProgramId.Key &&
                !k.StartsWith("Token") &&
                !k.StartsWith("11111") &&
                !k.StartsWith("Sysvar") &&
                k.Length == SolanaAddressLength)
            .Skip(2) // Skip signer and associated token account
            .FirstOrDefault();
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

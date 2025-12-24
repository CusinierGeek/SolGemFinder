using Microsoft.Extensions.Options;
using SolGemFinder.IndexerWorker.Models;
using Solnet.Rpc;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace SolGemFinder.IndexerWorker.Services;

public interface ISolanaRadar
{
    Task<List<PumpFunTokenInfo>> ScanForNewTokensAsync(CancellationToken ct = default);
}

public class SolanaRadar : ISolanaRadar
{
    private readonly ILogger<SolanaRadar> _logger;
    private readonly IRpcClient _rpcClient;
    private readonly PublicKey _pumpFunProgramId;
    private readonly SolanaRadarOptions _options;
    private ulong _lastProcessedSlot;
    private bool _hasLoggedApiKeyError = false;
    private readonly HashSet<string> _seenTokens = new();

    public SolanaRadar(ILogger<SolanaRadar> logger, IOptions<SolanaRadarOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        
        var rpcUrl = _options.RpcUrl;
        if (string.IsNullOrWhiteSpace(rpcUrl))
        {
            rpcUrl = Environment.GetEnvironmentVariable("HELIUS_RPC_URL");
            if (string.IsNullOrWhiteSpace(rpcUrl))
            {
                throw new InvalidOperationException("HELIUS_RPC_URL must be configured in appsettings or environment variable");
            }
        }

        _rpcClient = ClientFactory.GetClient(rpcUrl);
        _pumpFunProgramId = new PublicKey(_options.ProgramId);
        _lastProcessedSlot = 0;

        _logger.LogInformation("?? SolanaRadar initialized - ULTRA-FAST mode");
        _logger.LogInformation("   RPC: {RpcUrl}", MaskApiKey(rpcUrl));
        _logger.LogInformation("   Program ID: {ProgramId}", _options.ProgramId);
    }

    public async Task<List<PumpFunTokenInfo>> ScanForNewTokensAsync(CancellationToken ct = default)
    {
        var discoveries = new List<PumpFunTokenInfo>();

        try
        {
            // Get current slot
            var slotResponse = await _rpcClient.GetSlotAsync(Solnet.Rpc.Types.Commitment.Confirmed);
            
            if (!slotResponse.WasRequestSuccessfullyHandled)
            {
                // Check for specific error codes
                if (slotResponse.Reason?.Contains("-32401") == true || 
                    slotResponse.Reason?.Contains("invalid api key", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!_hasLoggedApiKeyError)
                    {
                        _logger.LogCritical("? Invalid Helius API key! Please check your configuration.");
                        _logger.LogCritical("   Update HELIUS_RPC_URL with a valid API key from https://helius.dev/");
                        _hasLoggedApiKeyError = true;
                    }
                    return discoveries;
                }

                _logger.LogWarning("Failed to get current slot: {Error}", slotResponse.Reason);
                return discoveries;
            }

            // Reset error flag on success
            _hasLoggedApiKeyError = false;

            var currentSlot = slotResponse.Result;
            
            // Initialize last processed slot on first run
            if (_lastProcessedSlot == 0)
            {
                _lastProcessedSlot = currentSlot - (ulong)_options.InitialSlotOffset;
                _logger.LogInformation("Starting slot scanning from slot {Slot}", _lastProcessedSlot);
            }

            // Don't scan if we're already up to date
            if (_lastProcessedSlot >= currentSlot)
            {
                return discoveries;
            }

            _logger.LogDebug("?? Scanning slots {LastSlot} ? {CurrentSlot}", _lastProcessedSlot, currentSlot);

            // Get signatures for the Pump.fun program
            var signaturesResponse = await _rpcClient.GetSignaturesForAddressAsync(
                _pumpFunProgramId.Key,
                limit: (ulong)_options.SignatureLimit,
                commitment: Solnet.Rpc.Types.Commitment.Confirmed
            );

            if (!signaturesResponse.WasRequestSuccessfullyHandled || signaturesResponse.Result == null)
            {
                _logger.LogWarning("Failed to get signatures: {Error}", signaturesResponse.Reason);
                return discoveries;
            }

            var newSignatures = signaturesResponse.Result
                .Where(s => s.Slot > _lastProcessedSlot && s.Slot <= currentSlot)
                .OrderBy(s => s.Slot)
                .ToList();

            if (newSignatures.Count > 0)
            {
                _logger.LogInformation("?? Found {Count} Pump.fun transactions to scan", newSignatures.Count);
            }

            int processed = 0;
            int filtered = 0;

            // Process each signature (lightweight filtering)
            foreach (var signature in newSignatures)
            {
                if (ct.IsCancellationRequested)
                    break;

                processed++;

                try
                {
                    var tokenInfo = await ProcessTransactionAsync(signature, ct);
                    if (tokenInfo != null)
                    {
                        // Deduplicate: skip if we've already seen this token
                        if (_seenTokens.Contains(tokenInfo.TokenAddress))
                        {
                            filtered++;
                            continue;
                        }

                        _seenTokens.Add(tokenInfo.TokenAddress);
                        discoveries.Add(tokenInfo);
                        
                        _logger.LogInformation("? NEW TOKEN: {TokenAddress} | Slot: {Slot}", 
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

            // Update last processed slot
            if (newSignatures.Any())
            {
                _lastProcessedSlot = newSignatures.Max(s => s.Slot);
            }
            else
            {
                _lastProcessedSlot = currentSlot;
            }

            // Log statistics
            if (processed > 0)
            {
                var rejectRate = filtered * 100 / processed;
                _logger.LogInformation("?? Processed: {Processed} | Kept: {Kept} | Filtered: {Filtered} ({Rate}%)", 
                    processed, discoveries.Count, filtered, rejectRate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning for new tokens");
        }

        return discoveries;
    }

    private async Task<PumpFunTokenInfo?> ProcessTransactionAsync(SignatureStatusInfo signatureInfo, CancellationToken ct)
    {
        // ?? NIVEAU 1 - FILTRES ULTRA-RAPIDES (micro-latence)
        
        // Filter 1: Transaction must be confirmed
        if (signatureInfo.ConfirmationStatus == null)
        {
            return null;
        }

        try
        {
            var txResponse = await _rpcClient.GetTransactionAsync(
                signatureInfo.Signature,
                commitment: Solnet.Rpc.Types.Commitment.Confirmed
            );

            if (!txResponse.WasRequestSuccessfullyHandled || txResponse.Result?.Transaction?.Message == null)
            {
                return null;
            }

            var transaction = txResponse.Result;
            var message = transaction.Transaction.Message;
            var accountKeys = message.AccountKeys.ToList();

            // Filter 2: Must have minimum accounts (token creation signature)
            if (accountKeys.Count < 8)
            {
                return null; // Token creation needs: system, token program, rent, mint, metadata, bonding curve, etc.
            }

            // Filter 3: Must involve SPL Token program
            var tokenProgramId = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
            if (!accountKeys.Contains(tokenProgramId))
            {
                return null;
            }

            // Filter 4: Must have System Program (needed for CreateAccount)
            var systemProgramId = "11111111111111111111111111111111";
            if (!accountKeys.Contains(systemProgramId))
            {
                return null;
            }

            // Filter 5: Check for token creation pattern in instructions
            var instructions = message.Instructions;
            bool hasCreateAccount = false;
            bool hasInitializeMint = false;

            foreach (var instruction in instructions)
            {
                var programId = accountKeys[(int)instruction.ProgramIdIndex];
                
                // System Program - CreateAccount instruction
                if (programId == systemProgramId && instruction.Data?.Length > 0)
                {
                    hasCreateAccount = true;
                }
                
                // Token Program - InitializeMint instruction
                if (programId == tokenProgramId && instruction.Data?.Length > 0)
                {
                    hasInitializeMint = true;
                }

                if (hasCreateAccount && hasInitializeMint)
                    break;
            }

            // Filter 6: Only keep transactions that look like token creation
            if (!hasCreateAccount || !hasInitializeMint)
            {
                return null; // Not a token creation, probably a trade
            }

            // Extract token mint address
            // In Pump.fun creation, mint is typically around index 4-6
            var potentialMintAddress = accountKeys
                .Skip(3)
                .Take(5)
                .FirstOrDefault(k => 
                    k != _pumpFunProgramId.Key && 
                    k != tokenProgramId && 
                    k != systemProgramId &&
                    !k.StartsWith("11111") && // Not system program variants
                    !k.StartsWith("Token") && // Not token program variants
                    !k.StartsWith("Sysvar")); // Not sysvar accounts

            if (string.IsNullOrEmpty(potentialMintAddress))
            {
                return null;
            }

            var timestamp = signatureInfo.BlockTime.HasValue 
                ? DateTimeOffset.FromUnixTimeSeconds((long)signatureInfo.BlockTime.Value).DateTime
                : DateTime.UtcNow;

            // ?? Token passed all filters - this looks like a real creation
            return new PumpFunTokenInfo
            {
                TokenAddress = potentialMintAddress,
                TransactionSignature = signatureInfo.Signature,
                Slot = signatureInfo.Slot,
                Timestamp = timestamp,
                IsPostBonding = false // Will be checked by DetectionWorker
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing transaction {Signature}", signatureInfo.Signature);
            return null;
        }
    }

    private static string MaskApiKey(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        var apiKeyIndex = url.IndexOf("api-key=", StringComparison.OrdinalIgnoreCase);
        if (apiKeyIndex == -1)
            return url;

        var keyStart = apiKeyIndex + 8;
        var keyEnd = url.IndexOf('&', keyStart);
        if (keyEnd == -1)
            keyEnd = url.Length;

        var maskedUrl = url.Substring(0, keyStart) + "***" + url.Substring(keyEnd);
        return maskedUrl;
    }
}

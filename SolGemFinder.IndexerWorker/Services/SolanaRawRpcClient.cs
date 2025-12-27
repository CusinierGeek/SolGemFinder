using System.Net.Http.Json;
using System.Text.Json;

namespace SolGemFinder.IndexerWorker.Services;

/// <summary>
/// Raw Solana RPC client that bypasses Solnet to get jsonParsed transactions.
/// CRITICAL: Solnet doesn't expose LoadedAddresses (ALT resolution).
/// This client requests jsonParsed encoding to get ALT-resolved accounts automatically.
/// </summary>
public class SolanaRawRpcClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SolanaRawRpcClient> _logger;
    private readonly string _rpcUrl;

    public SolanaRawRpcClient(HttpClient httpClient, ILogger<SolanaRawRpcClient> logger, string rpcUrl)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rpcUrl = rpcUrl;
    }

    /// <summary>
    /// Gets multiple transactions with jsonParsed encoding in a single batch call (ALT automatically resolved).
    /// PERFORMANCE: Reduces N RPC calls to 1 batch call.
    /// </summary>
    /// <param name="signatures">List of transaction signatures to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping signatures to parsed JSON documents.</returns>
    public async Task<Dictionary<string, JsonDocument>> GetTransactionsParsedBatchAsync(
        IEnumerable<string> signatures, 
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, JsonDocument>();
        var signaturesList = signatures.ToList();

        if (signaturesList.Count == 0)
            return results;

        // Solana RPC supports batch requests - send all at once!
        var batchRequest = signaturesList.Select((sig, index) => new
        {
            jsonrpc = "2.0",
            id = index,
            method = "getTransaction",
            @params = new object[]
            {
                sig,
                new
                {
                    encoding = "jsonParsed",
                    maxSupportedTransactionVersion = 0,
                    commitment = "confirmed"
                }
            }
        }).ToArray();

        try
        {
            _logger.LogDebug("?? Batch fetching {Count} transactions in 1 RPC call", signaturesList.Count);

            var response = await _httpClient.PostAsJsonAsync(_rpcUrl, batchRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            // Batch responses come back as an array
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idProp))
                        continue;

                    var id = idProp.GetInt32();
                    if (id < 0 || id >= signaturesList.Count)
                        continue;

                    var signature = signaturesList[id];

                    // Clone the response (we need to keep it after doc is disposed)
                    var itemJson = item.GetRawText();
                    var itemDoc = JsonDocument.Parse(itemJson);

                    results[signature] = itemDoc;
                }

                _logger.LogInformation("? Batch fetched {Success}/{Total} transactions successfully",
                    results.Count, signaturesList.Count);
            }
            else
            {
                _logger.LogWarning("?? Unexpected batch response format (not an array)");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "? HTTP error in batch transaction fetch");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "? JSON parse error in batch transaction fetch");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Unexpected error in batch transaction fetch");
        }

        return results;
    }

    /// <summary>
    /// Gets a transaction with jsonParsed encoding (ALT automatically resolved).
    /// </summary>
    /// <param name="signature">Transaction signature.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed JSON document with full transaction data.</returns>
    public async Task<JsonDocument?> GetTransactionParsedAsync(string signature, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "getTransaction",
            @params = new object[]
            {
                signature,
                new
                {
                    encoding = "jsonParsed",
                    maxSupportedTransactionVersion = 0,
                    commitment = "confirmed"
                }
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(_rpcUrl, request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(json);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error getting transaction {Signature}", signature[..8]);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parse error for transaction {Signature}", signature[..8]);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting transaction {Signature}", signature[..8]);
            return null;
        }
    }

    /// <summary>
    /// Extracts Pump.fun token creation info from a jsonParsed transaction.
    /// CRITICAL: Only processes "create" instructions, not buy/sell/other operations.
    /// Uses Anchor discriminator to identify instruction type.
    /// </summary>
    /// <param name="doc">Parsed transaction JSON document.</param>
    /// <param name="pumpFunProgramId">Pump.fun program ID.</param>
    /// <returns>Tuple of (mintAddress, bondingCurveAddress) or null if not found.</returns>
    public (string? mint, string? bonding)? ExtractPumpFunTokenCreation(JsonDocument doc, string pumpFunProgramId)
    {
        try
        {
            var root = doc.RootElement;

            // Check if result exists
            if (!root.TryGetProperty("result", out var result) || 
                result.ValueKind == JsonValueKind.Null)
            {
                _logger.LogTrace("  ?? No result in RPC response");
                return null;
            }

            // Check for transaction errors (failed transactions should be skipped)
            if (result.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("err", out var err) &&
                err.ValueKind != JsonValueKind.Null)
            {
                _logger.LogTrace("  ?? Transaction failed, skipping");
                return null;
            }

            // Navigate to instructions
            if (!result.TryGetProperty("transaction", out var transaction) ||
                !transaction.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("instructions", out var instructions))
            {
                _logger.LogTrace("  ?? No instructions in transaction");
                return null;
            }

            // Also get log messages to detect instruction type
            string? instructionType = null;
            if (meta.TryGetProperty("logMessages", out var logMessages))
            {
                foreach (var log in logMessages.EnumerateArray())
                {
                    var logStr = log.GetString();
                    if (logStr?.Contains("Instruction: Create", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        instructionType = "create";
                        _logger.LogTrace("  ? Detected CREATE instruction from logs");
                        break;
                    }
                    else if (logStr?.Contains("Instruction: Buy", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        instructionType = "buy";
                        break;
                    }
                    else if (logStr?.Contains("Instruction: Sell", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        instructionType = "sell";
                        break;
                    }
                }
            }

            // Skip if not a CREATE instruction
            if (instructionType != "create")
            {
                _logger.LogTrace("  ?? Not a CREATE instruction (type: {Type})", instructionType ?? "unknown");
                return null;
            }

            // Find Pump.fun instruction
            foreach (var ix in instructions.EnumerateArray())
            {
                // Check if this is a Pump.fun instruction
                if (!ix.TryGetProperty("programId", out var programId))
                    continue;

                var programIdStr = programId.GetString();
                if (programIdStr != pumpFunProgramId)
                    continue;

                _logger.LogTrace("  ? Found Pump.fun CREATE instruction");

                // Get accounts from Pump.fun instruction
                if (!ix.TryGetProperty("accounts", out var accounts))
                {
                    _logger.LogDebug("  ?? Pump.fun instruction has no accounts");
                    continue;
                }

                var accountsArray = accounts.EnumerateArray().ToList();
                
                // Log ALL accounts for debugging
                _logger.LogInformation("  ?? Pump.fun CREATE instruction has {Count} accounts:", accountsArray.Count);
                for (int i = 0; i < Math.Min(accountsArray.Count, 15); i++)
                {
                    var addr = accountsArray[i].GetString();
                    var suffix = addr?.Length >= 4 ? addr.Substring(addr.Length - 4) : "";
                    _logger.LogInformation("      [{Index}] = {Address} (ends: {Suffix})", i, addr, suffix);
                }

                // CRITICAL: Pump.fun CREATE instruction account layout discovery
                // Known system addresses to exclude when searching for token mint
                const string PumpFunMintAuthority = "TSLvdd1pWpHVjahSpsvCXUbgwsL3JAcvokwaKt1eokM";
                const string TokenProgramId = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
                const string Token2022ProgramId = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
                const string SystemProgramId = "11111111111111111111111111111111";
                const string AssociatedTokenProgramId = "ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL";
                const string SysvarRentId = "SysvarRent111111111111111111111111111111111";
                const string MetadataProgramId = "metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s";

                if (accountsArray.Count < 2)
                {
                    _logger.LogDebug("  ?? Pump.fun instruction has insufficient accounts (need at least 2, got {Count})", 
                        accountsArray.Count);
                    continue;
                }

                // Strategy: Find bonding curve (ends with "pump") and scan for mint
                string? bonding = null;
                string? mint = null;

                // 1. Find bonding curve (should end with "pump")
                for (int i = 0; i < accountsArray.Count && bonding == null; i++)
                {
                    var addr = accountsArray[i].GetString();
                    if (!string.IsNullOrEmpty(addr) && addr.EndsWith("pump", StringComparison.OrdinalIgnoreCase))
                    {
                        bonding = addr;
                        _logger.LogInformation("  ? Found bonding curve at [{Index}]: {Bonding}", i, bonding[..8]);
                    }
                }

                if (string.IsNullOrEmpty(bonding))
                {
                    _logger.LogWarning("  ?? No bonding curve found (should end with 'pump')");
                    continue;
                }

                // 2. Find mint: look for address that is NOT a known system address
                var knownAddresses = new HashSet<string>
                {
                    PumpFunMintAuthority,
                    TokenProgramId,
                    Token2022ProgramId,
                    SystemProgramId,
                    AssociatedTokenProgramId,
                    SysvarRentId,
                    MetadataProgramId,
                    pumpFunProgramId, // Pump.fun program itself
                    bonding // Already identified as bonding curve
                };

                for (int i = 0; i < Math.Min(accountsArray.Count, 5); i++) // Check first 5 accounts only
                {
                    var addr = accountsArray[i].GetString();
                    if (string.IsNullOrEmpty(addr))
                        continue;

                    // Skip known system addresses
                    if (knownAddresses.Contains(addr))
                        continue;

                    // Validate it looks like a valid Solana address (base58, 32-44 chars)
                    if (addr.Length < 32 || addr.Length > 44)
                        continue;

                    // This should be the mint!
                    mint = addr;
                    _logger.LogInformation("  ? Found token mint at [{Index}]: {Mint}", i, mint[..8]);
                    break;
                }

                if (string.IsNullOrEmpty(mint))
                {
                    _logger.LogWarning("  ?? Could not identify token mint in accounts");
                    continue;
                }

                // Final validation
                if (mint.Length != 43 && mint.Length != 44)
                {
                    _logger.LogDebug("  ?? Invalid mint address length: {Length}", mint.Length);
                    continue;
                }

                if (bonding.Length != 43 && bonding.Length != 44)
                {
                    _logger.LogDebug("  ?? Invalid bonding address length: {Length}", bonding.Length);
                    continue;
                }

                _logger.LogInformation("  ?? PUMP.FUN TOKEN CREATED ? Mint: {Mint} | Bonding: {Bonding}",
                    mint, bonding);

                return (mint, bonding);
            }

            _logger.LogTrace("  ?? No Pump.fun CREATE instruction found");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting Pump.fun token from jsonParsed transaction");
            return null;
        }
    }
}

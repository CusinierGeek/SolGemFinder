using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SolGemFinder.Shared.Services.BitQuery;

/// <summary>
/// JSON converter that handles both string and number formats for decimal values.
/// </summary>
public class FlexibleDecimalConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? "0";
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDecimal().ToString();
        }
        return "0";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Extended APIs for BitQueryPumpFunService - Token metadata, holders, trades.
/// </summary>
public partial class BitQueryPumpFunService
{
    /// <summary>
    /// Gets token metadata using DEXTradeByTokens query.
    /// </summary>
    public async Task<TokenMetadata?> GetTokenMetadataAsync(string mintAddress, CancellationToken ct = default)
    {
        _logger.LogDebug("📊 Getting token metadata: {Mint}", mintAddress[..8]);

        var query = $@"
query GetTokenMetadata {{
  Solana {{
    DEXTradeByTokens(
      where: {{Trade: {{Currency: {{MintAddress: {{is: ""{mintAddress}""}}}}}}}}
      orderBy: {{descending: Block_Time}}
      limit: {{count: 1}}
    ) {{
      Trade {{
        Currency {{
          Uri
          UpdateAuthority
          Name
          IsMutable
          Symbol
          MintAddress
        }}
      }}
    }}
  }}
}}";

        try
        {
            var response = await ExecuteQueryAsync<TokenMetadataResponse>(query, ct);
            var trade = response?.Solana?.DEXTradeByTokens?.FirstOrDefault();

            if (trade?.Trade?.Currency == null)
            {
                _logger.LogDebug("  ⏭️ No metadata found for {Mint}", mintAddress[..8]);
                return null;
            }

            var currency = trade.Trade.Currency;

            return new TokenMetadata
            {
                MintAddress = currency.MintAddress ?? mintAddress,
                Name = currency.Name,
                Symbol = currency.Symbol,
                Uri = currency.Uri,
                UpdateAuthority = currency.UpdateAuthority,
                IsMutable = currency.IsMutable
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting token metadata: {Mint}", mintAddress[..8]);
            return null;
        }
    }

    /// <summary>
    /// Gets top token holders using BalanceUpdates query.
    /// </summary>
    public async Task<List<TokenHolder>> GetTopHoldersAsync(string mintAddress, int limit = 10, CancellationToken ct = default)
    {
        _logger.LogDebug("📊 Getting top {Limit} holders: {Mint}", limit, mintAddress[..8]);

        var query = $@"
query GetTopHolders {{
  Solana {{
    BalanceUpdates(
      orderBy: {{descendingByField: ""BalanceUpdate_Holding_maximum""}}
      where: {{BalanceUpdate: {{Currency: {{MintAddress: {{is: ""{mintAddress}""}}}}}}}}
      limit: {{count: {limit}}}
    ) {{
      BalanceUpdate {{
        Currency {{
          Name
          MintAddress
          Symbol
        }}
        Account {{
          Address
        }}
        Holding: PostBalance(maximum: Block_Slot, selectWhere: {{gt: ""0""}})
      }}
    }}
  }}
}}";

        try
        {
            var response = await ExecuteQueryAsync<TopHoldersResponse>(query, ct);
            var holders = response?.Solana?.BalanceUpdates ?? new List<HolderUpdate>();

            return holders.Select(h => new TokenHolder
            {
                Address = h.BalanceUpdate.Account?.Address ?? "Unknown",
                Balance = SafeParseDecimal(h.BalanceUpdate.Holding),
                PercentageOfSupply = null // Calculated separately if needed
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting top holders: {Mint}", mintAddress[..8]);
            return new List<TokenHolder>();
        }
    }

    /// <summary>
    /// Gets recent trades for a token.
    /// </summary>
    public async Task<List<TokenTrade>> GetRecentTradesAsync(string mintAddress, int limit = 20, CancellationToken ct = default)
    {
        _logger.LogDebug("📊 Getting recent {Limit} trades: {Mint}", limit, mintAddress[..8]);

        var query = $@"
query GetRecentTrades {{
  Solana {{
    DEXTradeByTokens(
      orderBy: {{descending: Block_Time}}
      limit: {{count: {limit}}}
      where: {{Trade: {{Currency: {{MintAddress: {{is: ""{mintAddress}""}}}}, Side: {{Currency: {{MintAddress: {{in: ["""", ""So11111111111111111111111111111111111111112"", ""EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v""]}}}}}}}}}}
    ) {{
      Block {{
        Time
      }}
      Transaction {{
        Signature
      }}
      Trade {{
        AmountInUSD
        PriceInUSD
        Amount
        Currency {{
          Name
        }}
        Dex {{
          ProtocolName
          ProtocolFamily
        }}
        Side {{
          Type
          Currency {{
            Symbol
            MintAddress
          }}
          AmountInUSD
          Amount
        }}
      }}
    }}
  }}
}}";

        try
        {
            var response = await ExecuteQueryAsync<RecentTradesResponse>(query, ct);
            var trades = response?.Solana?.DEXTradeByTokens ?? new List<TradeData>();

            return trades.Select(t => new TokenTrade
            {
                Time = t.Block?.Time ?? DateTime.UtcNow,
                Signature = t.Transaction?.Signature ?? "Unknown",
                Amount = SafeParseDecimal(t.Trade?.Amount),
                AmountInUSD = SafeParseDecimal(t.Trade?.AmountInUSD),
                PriceInUSD = SafeParseDecimal(t.Trade?.PriceInUSD),
                DexName = t.Trade?.Dex?.ProtocolName,
                Side = t.Trade?.Side?.Type
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent trades: {Mint}", mintAddress[..8]);
            return new List<TokenTrade>();
        }
    }

    /// <summary>
    /// Safely parses a decimal value with invariant culture (handles both "." and "," as decimal separator).
    /// </summary>
    private static decimal SafeParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0m;

        try
        {
            return decimal.Parse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0m;
        }
    }
}

#region Response Models

// Token Metadata Response
public class TokenMetadataResponse
{
    public TokenMetadataSolana? Solana { get; set; }
}

public class TokenMetadataSolana
{
    public List<TokenMetadataTrade>? DEXTradeByTokens { get; set; }
}

public class TokenMetadataTrade
{
    public TokenMetadataTradeInfo? Trade { get; set; }
}

public class TokenMetadataTradeInfo
{
    public TokenMetadataCurrency? Currency { get; set; }
}

public class TokenMetadataCurrency
{
    public string? Uri { get; set; }
    public string? UpdateAuthority { get; set; }
    public string? Name { get; set; }
    public bool IsMutable { get; set; }
    public string? Symbol { get; set; }
    public string? MintAddress { get; set; }
}

// Top Holders Response
public class TopHoldersResponse
{
    public TopHoldersSolana? Solana { get; set; }
}

public class TopHoldersSolana
{
    public List<HolderUpdate>? BalanceUpdates { get; set; }
}

public class HolderUpdate
{
    public HolderBalanceUpdate BalanceUpdate { get; set; } = new();
}

public class HolderBalanceUpdate
{
    public HolderCurrency? Currency { get; set; }
    public HolderAccount? Account { get; set; }
    public string? Holding { get; set; }
}

public class HolderCurrency
{
    public string? Name { get; set; }
    public string? MintAddress { get; set; }
    public string? Symbol { get; set; }
}

public class HolderAccount
{
    public string? Address { get; set; }
}

// Recent Trades Response
public class RecentTradesResponse
{
    public RecentTradesSolana? Solana { get; set; }
}

public class RecentTradesSolana
{
    public List<TradeData>? DEXTradeByTokens { get; set; }
}

public class TradeData
{
    public TradeBlock? Block { get; set; }
    public TradeTransaction? Transaction { get; set; }
    public TradeInfo? Trade { get; set; }
}

public class TradeBlock
{
    public DateTime Time { get; set; }
}

public class TradeTransaction
{
    public string? Signature { get; set; }
}

public class TradeInfo
{
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public string? AmountInUSD { get; set; }
    
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public string? PriceInUSD { get; set; }
    
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public string? Amount { get; set; }
    
    public TradeCurrency? Currency { get; set; }
    public TradeDex? Dex { get; set; }
    public TradeSide? Side { get; set; }
}

public class TradeCurrency
{
    public string? Name { get; set; }
}

public class TradeDex
{
    public string? ProtocolName { get; set; }
    public string? ProtocolFamily { get; set; }
}

public class TradeSide
{
    public string? Type { get; set; }
    public TradeSideCurrency? Currency { get; set; }
    
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public string? AmountInUSD { get; set; }
    
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public string? Amount { get; set; }
}

public class TradeSideCurrency
{
    public string? Symbol { get; set; }
    public string? MintAddress { get; set; }
}

#endregion

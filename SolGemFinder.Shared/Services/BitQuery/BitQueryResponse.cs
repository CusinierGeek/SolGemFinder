using System.Text.Json.Serialization;

namespace SolGemFinder.Shared.Services.BitQuery;

/// <summary>
/// Root response from BitQuery GraphQL API.
/// </summary>
public class BitQueryResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<BitQueryError>? Errors { get; set; }
}

/// <summary>
/// BitQuery error information.
/// </summary>
public class BitQueryError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("locations")]
    public List<ErrorLocation>? Locations { get; set; }

    [JsonPropertyName("path")]
    public List<string>? Path { get; set; }
}

/// <summary>
/// Error location in GraphQL query.
/// </summary>
public class ErrorLocation
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>
/// Data wrapper for Solana queries.
/// </summary>
public class SolanaData
{
    [JsonPropertyName("Solana")]
    public SolanaQuery? Solana { get; set; }
}

/// <summary>
/// Solana query containing DEX pools.
/// </summary>
public class SolanaQuery
{
    [JsonPropertyName("DEXPools")]
    public List<DexPoolToken>? DexPools { get; set; }
}

/// <summary>
/// DEX pool information for a token with bonding curve progress.
/// </summary>
public class DexPoolToken
{
    [JsonPropertyName("Bonding_Curve_Progress_precentage")]
    public decimal BondingCurveProgressPercentage { get; set; }

    [JsonPropertyName("Pool")]
    public PoolInfo? Pool { get; set; }
}

/// <summary>
/// Pool information including market, base, and quote details.
/// </summary>
public class PoolInfo
{
    [JsonPropertyName("Market")]
    public MarketInfo? Market { get; set; }

    [JsonPropertyName("Dex")]
    public DexInfo? Dex { get; set; }

    [JsonPropertyName("Base")]
    public BaseInfo? Base { get; set; }

    [JsonPropertyName("Quote")]
    public QuoteInfo? Quote { get; set; }
}

/// <summary>
/// Market information for the pool.
/// </summary>
public class MarketInfo
{
    [JsonPropertyName("BaseCurrency")]
    public CurrencyInfo? BaseCurrency { get; set; }

    [JsonPropertyName("QuoteCurrency")]
    public CurrencyInfo? QuoteCurrency { get; set; }

    [JsonPropertyName("MarketAddress")]
    public string MarketAddress { get; set; } = string.Empty;
}

/// <summary>
/// Currency (token) information.
/// </summary>
public class CurrencyInfo
{
    [JsonPropertyName("MintAddress")]
    public string MintAddress { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Symbol")]
    public string? Symbol { get; set; }
}

/// <summary>
/// DEX information.
/// </summary>
public class DexInfo
{
    [JsonPropertyName("ProtocolName")]
    public string ProtocolName { get; set; } = string.Empty;

    [JsonPropertyName("ProtocolFamily")]
    public string ProtocolFamily { get; set; } = string.Empty;
}

/// <summary>
/// Base token information in the pool.
/// </summary>
public class BaseInfo
{
    [JsonPropertyName("Balance")]
    public string Balance { get; set; } = string.Empty;
}

/// <summary>
/// Quote token (SOL) information in the pool.
/// </summary>
public class QuoteInfo
{
    [JsonPropertyName("PostAmount")]
    public string PostAmount { get; set; } = string.Empty;

    [JsonPropertyName("PostAmountInUSD")]
    public string? PostAmountInUSD { get; set; }

    [JsonPropertyName("PriceInUSD")]
    public decimal PriceInUSD { get; set; }
}

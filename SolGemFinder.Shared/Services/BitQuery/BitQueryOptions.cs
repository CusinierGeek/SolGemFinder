namespace SolGemFinder.Shared.Services.BitQuery;

/// <summary>
/// Configuration options for BitQuery API integration.
/// </summary>
public class BitQueryOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "BitQuery";

    /// <summary>
    /// BitQuery GraphQL API endpoint.
    /// Default: https://streaming.bitquery.io/eap
    /// </summary>
    public string ApiEndpoint { get; set; } = "https://streaming.bitquery.io/eap";

    /// <summary>
    /// BitQuery OAuth token for authentication.
    /// Can be overridden by BITQUERY_TOKEN environment variable.
    /// </summary>
    public string OAuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Time window in minutes to query for graduating tokens.
    /// Default: 5 minutes
    /// </summary>
    public int QueryWindowMinutes { get; set; } = 5;

    /// <summary>
    /// Minimum bonding curve progress percentage to consider (0-100).
    /// Default: 95% (tokens about to graduate)
    /// </summary>
    public decimal MinBondingProgressPercent { get; set; } = 95m;

    /// <summary>
    /// Maximum number of tokens to fetch per query.
    /// Default: 100
    /// </summary>
    public int MaxTokensPerQuery { get; set; } = 100;

    /// <summary>
    /// Timeout in seconds for API requests.
    /// Default: 30 seconds
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts for failed API calls.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds between retry attempts.
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 1000;
}

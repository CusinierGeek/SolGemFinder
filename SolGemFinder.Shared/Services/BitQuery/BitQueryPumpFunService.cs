using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SolGemFinder.Shared.Services.BitQuery;

/// <summary>
/// BitQuery PumpFun service for discovering high-quality tokens using GraphQL queries.
/// Replaces blockchain scanning with targeted API queries for reduced noise and cost.
/// </summary>
public partial class BitQueryPumpFunService : IBitQueryPumpFunService
{
    private readonly ILogger<BitQueryPumpFunService> _logger;
    private readonly HttpClient _httpClient;
    private readonly BitQueryOptions _options;
    private readonly HashSet<string> _seenTokens;
    private const string PumpFunProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";

    public BitQueryPumpFunService(
        ILogger<BitQueryPumpFunService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<BitQueryOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _httpClient = httpClientFactory.CreateClient("BitQuery");
        _seenTokens = new HashSet<string>(10000);

        ConfigureHttpClient();

        _logger.LogInformation("?? BitQuery PumpFun Service initialized");
        _logger.LogInformation("   API Endpoint: {Endpoint}", _options.ApiEndpoint);
        _logger.LogInformation("   Query Window: {Minutes} minutes", _options.QueryWindowMinutes);
        _logger.LogInformation("   Min Bonding Progress: {Percent}%", _options.MinBondingProgressPercent);
        _logger.LogInformation("   Max Tokens Per Query: {Max}", _options.MaxTokensPerQuery);
    }

    private void ConfigureHttpClient()
    {
        var token = GetOAuthToken();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("BitQuery OAuth token is required. Set BITQUERY_TOKEN environment variable.");
        }

        _httpClient.BaseAddress = new Uri(_options.ApiEndpoint);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", token);
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
    }

    private string GetOAuthToken()
    {
        var token = _options.OAuthToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("BITQUERY_TOKEN");
        }
        return token ?? string.Empty;
    }

    public async Task<List<PumpFunTokenInfo>> DiscoverTokensAsync(
        TokenDiscoveryStrategy strategy = TokenDiscoveryStrategy.AboutToGraduate,
        CancellationToken ct = default)
    {
        _logger.LogInformation("?? Discovering tokens using strategy: {Strategy}", strategy);

        var query = strategy switch
        {
            TokenDiscoveryStrategy.AboutToGraduate => BuildAboutToGraduateQuery(),
            TokenDiscoveryStrategy.RecentlyGraduated => BuildRecentlyGraduatedQuery(),
            TokenDiscoveryStrategy.HighPriceMomentum => BuildHighPriceMomentumQuery(),
            TokenDiscoveryStrategy.HighMarketCapGrowth => BuildHighMarketCapGrowthQuery(),
            TokenDiscoveryStrategy.TopByMarketCap => BuildTopByMarketCapQuery(),
            TokenDiscoveryStrategy.AllNewTokens => BuildAllNewTokensQuery(),
            _ => BuildAboutToGraduateQuery()
        };

        try
        {
            var response = await ExecuteQueryAsync<SolanaData>(query, ct);
            if (response?.Solana?.DexPools == null)
            {
                _logger.LogWarning("No tokens returned from BitQuery for strategy: {Strategy}", strategy);
                return new List<PumpFunTokenInfo>();
            }

            var tokens = ProcessDexPoolTokens(response.Solana.DexPools, strategy);
            _logger.LogInformation("? Discovered {Count} new tokens using {Strategy}", tokens.Count, strategy);
            return tokens;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "? Error discovering tokens with strategy: {Strategy}", strategy);
            return new List<PumpFunTokenInfo>();
        }
    }

    public async Task<decimal?> GetBondingProgressAsync(string mintAddress, CancellationToken ct = default)
    {
        var query = BuildBondingProgressQuery(mintAddress);

        try
        {
            var response = await ExecuteQueryAsync<SolanaData>(query, ct);
            if (response?.Solana?.DexPools?.FirstOrDefault() is { } pool)
            {
                return Math.Round(pool.BondingCurveProgressPercentage, 2);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting bonding progress for {Mint}", mintAddress);
            return null;
        }
    }

    public async Task<bool> HasGraduatedAsync(string mintAddress, CancellationToken ct = default)
    {
        var query = BuildGraduationCheckQuery(mintAddress);

        try
        {
            var response = await ExecuteQueryAsync<SolanaData>(query, ct);
            // If token has pools on Raydium/Orca, it has graduated
            var hasGraduated = response?.Solana?.DexPools?.Any() ?? false;

            return hasGraduated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking graduation status for {Mint}", mintAddress);
            return false;
        }
    }

    private async Task<T?> ExecuteQueryAsync<T>(string query, CancellationToken ct) where T : class
    {
        for (int attempt = 1; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            try
            {
                var payload = new { query };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("", content, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("BitQuery API returned {Status}: {Body}",
                        response.StatusCode, responseBody);

                    if (attempt < _options.MaxRetryAttempts)
                    {
                        await Task.Delay(_options.RetryDelayMilliseconds * attempt, ct);
                        continue;
                    }

                    return default;
                }

                var result = JsonSerializer.Deserialize<BitQueryResponse<T>>(responseBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.Errors?.Any() == true)
                {
                    foreach (var error in result.Errors)
                    {
                        _logger.LogWarning("BitQuery GraphQL Error: {Message}", error.Message);
                    }
                }

                return result?.Data;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Attempt {Attempt}/{Max} failed", attempt, _options.MaxRetryAttempts);

                if (attempt < _options.MaxRetryAttempts)
                {
                    await Task.Delay(_options.RetryDelayMilliseconds * attempt, ct);
                }
                else
                {
                    throw;
                }
            }
        }

        return default;
    }

    private List<PumpFunTokenInfo> ProcessDexPoolTokens(List<DexPoolToken> pools, TokenDiscoveryStrategy strategy)
    {
        var tokens = new List<PumpFunTokenInfo>();
        var now = DateTime.UtcNow;

        foreach (var pool in pools)
        {
            if (pool.Pool?.Market?.BaseCurrency == null) continue;

            var mintAddress = pool.Pool.Market.BaseCurrency.MintAddress;
            if (string.IsNullOrEmpty(mintAddress) || _seenTokens.Contains(mintAddress))
            {
                continue;
            }

            // Parse liquidity values
            decimal liquiditySOL = 0;
            decimal liquidityUSD = 0;
            if (pool.Pool.Quote != null)
            {
                decimal.TryParse(pool.Pool.Quote.PostAmount, out liquiditySOL);
                if (!string.IsNullOrEmpty(pool.Pool.Quote.PostAmountInUSD))
                {
                    decimal.TryParse(pool.Pool.Quote.PostAmountInUSD, out liquidityUSD);
                }
            }

            var bondingProgress = Math.Round(pool.BondingCurveProgressPercentage, 2);

            var tokenInfo = new PumpFunTokenInfo
            {
                TokenAddress = mintAddress,
                TransactionSignature = $"bitquery-{strategy.ToString().ToLowerInvariant()}-{mintAddress[..8]}",
                Slot = 0, // Not applicable for BitQuery
                Timestamp = now,
                TokenName = pool.Pool.Market.BaseCurrency.Name,
                TokenSymbol = pool.Pool.Market.BaseCurrency.Symbol,
                BondingCurveAddress = pool.Pool.Market.MarketAddress,
                BondingCurveProgress = bondingProgress,
                IsPostBonding = strategy == TokenDiscoveryStrategy.RecentlyGraduated || bondingProgress >= 100,
                LiquidityInSOL = liquiditySOL > 0 ? liquiditySOL : null,
                LiquidityInUSD = liquidityUSD > 0 ? liquidityUSD : null
            };

            tokens.Add(tokenInfo);
            _seenTokens.Add(mintAddress);

            _logger.LogDebug("?? Found token: {Symbol} ({Mint}) | Bonding: {Progress}% | Liquidity: {SOL} SOL (${USD})",
                tokenInfo.TokenSymbol ?? "Unknown",
                mintAddress[..8],
                bondingProgress,
                liquiditySOL.ToString("F2"),
                liquidityUSD.ToString("F0"));
        }

        return tokens;
    }

    #region GraphQL Queries

    private string BuildAboutToGraduateQuery()
    {
        var minutesAgo = _options.QueryWindowMinutes;
        var minProgress = _options.MinBondingProgressPercent;
        
        // Calculate minimum base balance for the desired progress percentage
        // Formula: BaseBalance = 206900000 + ((100 - progress) * 793100000 / 100)
        // For 95%: 206900000 + (5 * 793100000 / 100) = 246555000
        var minBaseBalance = 206900000m + ((100 - minProgress) * 793100000m / 100);

        return $@"
{{
  Solana {{
    DEXPools(
      limitBy: {{by: Pool_Market_BaseCurrency_MintAddress, count: 1}}
      limit: {{count: {_options.MaxTokensPerQuery}}}
      orderBy: {{ascending: Pool_Base_PostAmount}}
      where: {{
        Pool: {{
          Base: {{PostAmount: {{gt: ""{minBaseBalance}""}}}},
          Dex: {{ProgramAddress: {{is: ""6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P""}}}},
          Market: {{QuoteCurrency: {{MintAddress: {{in: [""11111111111111111111111111111111"", ""So11111111111111111111111111111111111111112""]}}}}}}
        }},
        Transaction: {{Result: {{Success: true}}}},
        Block: {{Time: {{since_relative: {{minutes_ago: {minutesAgo}}}}}}}
      }}
    ) {{
      Bonding_Curve_Progress_precentage: calculate(expression: ""100 - ((($Pool_Base_Balance - 206900000) * 100) / 793100000)"")
      Pool {{
        Market {{
          BaseCurrency {{
            MintAddress
            Name
            Symbol
          }}
          MarketAddress
          QuoteCurrency {{
            MintAddress
            Name
            Symbol
          }}
        }}
        Dex {{
          ProtocolName
          ProtocolFamily
        }}
        Base {{
          Balance: PostAmount(maximum: Block_Time)
        }}
        Quote {{
          PostAmount
          PriceInUSD
          PostAmountInUSD
        }}
      }}
    }}
  }}
}}";
    }

    private string BuildRecentlyGraduatedQuery()
    {
        var minutesAgo = _options.QueryWindowMinutes;

        return $@"
{{
  Solana {{
    DEXPools(
      limitBy: {{by: Pool_Market_BaseCurrency_MintAddress, count: 1}}
      limit: {{count: {_options.MaxTokensPerQuery}}}
      orderBy: {{descending: Pool_Quote_PostAmountInUSD}}
      where: {{
        Pool: {{
          Dex: {{ProtocolName: {{in: [""raydium"", ""orca""]}}}},
          Market: {{BaseCurrency: {{MintAddress: {{not: """"}}}}}}
        }},
        Transaction: {{Result: {{Success: true}}}},
        Block: {{Time: {{since_relative: {{minutes_ago: {minutesAgo}}}}}}}
      }}
    ) {{
      Bonding_Curve_Progress_precentage: calculate(expression: ""100.0"")
      Pool {{
        Market {{
          BaseCurrency {{
            MintAddress
            Name
            Symbol
          }}
          MarketAddress
          QuoteCurrency {{
            MintAddress
            Name
            Symbol
          }}
        }}
        Dex {{
          ProtocolName
          ProtocolFamily
        }}
        Base {{
          Balance: PostAmount(maximum: Block_Time)
        }}
        Quote {{
          PostAmount
          PriceInUSD
          PostAmountInUSD
        }}
      }}
    }}
  }}
}}";
    }

    private string BuildHighPriceMomentumQuery()
    {
        var minutesAgo = 5;

        return $@"
{{
  Solana {{
    DEXPools(
      limitBy: {{by: Pool_Market_BaseCurrency_MintAddress, count: 1}}
      limit: {{count: {_options.MaxTokensPerQuery}}}
      orderBy: {{ascending: Pool_Base_PostAmount}}
      where: {{
        Pool: {{
          Base: {{PostAmount: {{gt: ""206900000""}}}},
          Dex: {{ProgramAddress: {{is: ""6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P""}}}},
          Market: {{QuoteCurrency: {{MintAddress: {{in: [""11111111111111111111111111111111"", ""So11111111111111111111111111111111111111112""]}}}}}}
        }},
        Transaction: {{Result: {{Success: true}}}},
        Block: {{Time: {{since_relative: {{minutes_ago: {minutesAgo}}}}}}}
      }}
    ) {{
      Bonding_Curve_Progress_precentage: calculate(expression: ""100 - ((($Pool_Base_Balance - 206900000) * 100) / 793100000)"")
      Pool {{
        Market {{
          BaseCurrency {{
            MintAddress
            Name
            Symbol
          }}
          MarketAddress
          QuoteCurrency {{
            MintAddress
            Name
            Symbol
          }}
        }}
        Dex {{
          ProtocolName
          ProtocolFamily
        }}
        Base {{
          Balance: PostAmount(maximum: Block_Time)
        }}
        Quote {{
          PostAmount
          PriceInUSD
          PostAmountInUSD
        }}
      }}
    }}
  }}
}}";
    }

    private string BuildHighMarketCapGrowthQuery()
    {
        return BuildHighPriceMomentumQuery();
    }

    private string BuildTopByMarketCapQuery()
    {
        var hoursAgo = 24;

        return $@"
{{
  Solana {{
    DEXPools(
      limitBy: {{by: Pool_Market_BaseCurrency_MintAddress, count: 1}}
      limit: {{count: {_options.MaxTokensPerQuery}}}
      orderBy: {{descending: Pool_Quote_PostAmountInUSD}}
      where: {{
        Pool: {{
          Base: {{PostAmount: {{gt: ""206900000""}}}},
          Dex: {{ProgramAddress: {{is: ""6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P""}}}},
          Market: {{QuoteCurrency: {{MintAddress: {{in: [""11111111111111111111111111111111"", ""So11111111111111111111111111111111111111112""]}}}}}}
        }},
        Transaction: {{Result: {{Success: true}}}},
        Block: {{Time: {{since_relative: {{hours_ago: {hoursAgo}}}}}}}
      }}
    ) {{
      Bonding_Curve_Progress_precentage: calculate(expression: ""100 - ((($Pool_Base_Balance - 206900000) * 100) / 793100000)"")
      Pool {{
        Market {{
          BaseCurrency {{
            MintAddress
            Name
            Symbol
          }}
          MarketAddress
          QuoteCurrency {{
            MintAddress
            Name
            Symbol
          }}
        }}
        Dex {{
          ProtocolName
          ProtocolFamily
        }}
        Base {{
          Balance: PostAmount(maximum: Block_Time)
        }}
        Quote {{
          PostAmount
          PriceInUSD
          PostAmountInUSD
        }}
      }}
    }}
  }}
}}";
    }

    private string BuildAllNewTokensQuery()
    {
        var minutesAgo = _options.QueryWindowMinutes;

        return $@"
{{
  Solana {{
    DEXPools(
      limitBy: {{by: Pool_Market_BaseCurrency_MintAddress, count: 1}}
      limit: {{count: {_options.MaxTokensPerQuery}}}
      orderBy: {{descending: Block_Time}}
      where: {{
        Pool: {{
          Dex: {{ProgramAddress: {{is: ""6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P""}}}},
          Market: {{QuoteCurrency: {{MintAddress: {{in: [""11111111111111111111111111111111"", ""So11111111111111111111111111111111111111112""]}}}}}}
        }},
        Transaction: {{Result: {{Success: true}}}},
        Block: {{Time: {{since_relative: {{minutes_ago: {minutesAgo}}}}}}}
      }}
    ) {{
      Bonding_Curve_Progress_precentage: calculate(expression: ""100 - ((($Pool_Base_Balance - 206900000) * 100) / 793100000)"")
      Pool {{
        Market {{
          BaseCurrency {{
            MintAddress
            Name
            Symbol
          }}
          MarketAddress
          QuoteCurrency {{
            MintAddress
            Name
            Symbol
          }}
        }}
        Dex {{
          ProtocolName
          ProtocolFamily
        }}
        Base {{
          Balance: PostAmount(maximum: Block_Time)
        }}
        Quote {{
          PostAmount
          PriceInUSD
          PostAmountInUSD
        }}
      }}
    }}
  }}
}}";
    }

    private string BuildBondingProgressQuery(string mintAddress)
    {
        var hoursAgo = 1;

        return $@"
{{
  Solana {{
    DEXPools(
      where: {{
        Pool: {{
          Dex: {{ProgramAddress: {{is: ""6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P""}}}},
          Market: {{
            BaseCurrency: {{MintAddress: {{is: ""{mintAddress}""}}}},
            QuoteCurrency: {{MintAddress: {{in: [""11111111111111111111111111111111"", ""So11111111111111111111111111111111111111112""]}}}}
          }}
        }},
        Transaction: {{Result: {{Success: true}}}},
        Block: {{Time: {{since_relative: {{hours_ago: {hoursAgo}}}}}}}
      }}
    ) {{
      Bonding_Curve_Progress_precentage: calculate(expression: ""100 - ((($Pool_Base_Balance - 206900000) * 100) / 793100000)"")
      Pool {{
        Market {{
          BaseCurrency {{
            MintAddress
          }}
        }}
        Base {{
          Balance: PostAmount(maximum: Block_Time)
        }}
        Quote {{
          PostAmount
        }}
      }}
    }}
  }}
}}";
    }

    private string BuildGraduationCheckQuery(string mintAddress)
    {
        var hoursAgo = 24;

        return $@"
{{
  Solana {{
    DEXPools(
      where: {{
        Pool: {{
          Dex: {{ProtocolName: {{in: [""raydium"", ""orca""]}}}},
          Market: {{BaseCurrency: {{MintAddress: {{is: ""{mintAddress}""}}}}}}
        }},
        Transaction: {{Result: {{Success: true}}}},
        Block: {{Time: {{since_relative: {{hours_ago: {hoursAgo}}}}}}}
      }}
    ) {{
      Bonding_Curve_Progress_precentage: calculate(expression: ""100.0"")
      Pool {{
        Market {{
          BaseCurrency {{
            MintAddress
          }}
          MarketAddress
        }}
      }}
    }}
  }}
}}";
    }

    #endregion
}

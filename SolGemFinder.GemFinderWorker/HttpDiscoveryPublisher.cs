using SolGemFinder.ScoringWorker;

public class HttpDiscoveryPublisher : IDiscoveryPublisher
{
    private readonly HttpClient _http;
    private readonly string _tradingUrl;

    public HttpDiscoveryPublisher(IHttpClientFactory httpFactory, IConfiguration config)
    {
        _http = httpFactory.CreateClient();
        _tradingUrl = config.GetValue<string>("ServiceBus:HttpFallbackUrl") ?? System.Environment.GetEnvironmentVariable("TRADING_URL") ?? "http://localhost:5002/discoveries";
    }

    public async Task PublishAsync<T>(T payload, CancellationToken ct = default)
    {
        await _http.PostAsJsonAsync(_tradingUrl, payload, ct);
    }
}

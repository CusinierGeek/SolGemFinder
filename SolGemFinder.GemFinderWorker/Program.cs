using Azure.Messaging.ServiceBus;
using SolGemFinder.ScoringWorker;
using SolGemFinder.ScoringWorker.Services;
using SolGemFinder.Shared.Services.BitQuery;
using StackExchange.Redis;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.ConfigureServices((context, services) =>
        {
            IConfiguration config = context.Configuration;
            var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Startup");

            // ========== Redis Configuration ==========
            var redisConnection = config.GetConnectionString("redis") 
                ?? Environment.GetEnvironmentVariable("ConnectionStrings__redis")
                ?? "localhost:6379";

            logger.LogInformation("🔧 Configuring Redis: {Connection}", redisConnection);

            try
            {
                var redisMultiplexer = ConnectionMultiplexer.Connect(redisConnection);
                services.AddSingleton<IConnectionMultiplexer>(redisMultiplexer);
                services.AddSingleton<ITokenCacheService, RedisCacheService>();
                
                logger.LogInformation("✅ Redis connected successfully");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "❌ Failed to connect to Redis. Cache will not be available.");
                throw;
            }

            // ========== Service Bus Configuration ==========
            var serviceBusConnection = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION");

            if (string.IsNullOrWhiteSpace(serviceBusConnection))
            {
                logger.LogCritical("ServiceBus connection string is not configured. Set SERVICEBUS_CONNECTION environment variable.");
                throw new InvalidOperationException("ServiceBus connection string is required");
            }

            services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            services.AddSingleton<ServiceBusDiscoveryPublisher>();
            services.AddSingleton<IDiscoveryPublisher>(sp => sp.GetRequiredService<ServiceBusDiscoveryPublisher>());

            // ========== BitQuery Configuration ==========
            services.Configure<BitQueryOptions>(config.GetSection(BitQueryOptions.SectionName));

            services.AddHttpClient("BitQuery")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });

            services.AddSingleton<IBitQueryPumpFunService, BitQueryPumpFunService>();

            logger.LogInformation("✅ BitQuery PumpFun Service configured");

            // ========== Scoring Services ==========
            services.AddSingleton<OnChainValidator>();
            services.AddSingleton<TokenScoringEngine>();
            services.AddSingleton<LiquidityValidator>();
            services.AddSingleton<PumpFunBondingDetector>();
            services.AddSingleton<TradingPublisher>();
            services.AddSingleton<ScoringConsumer>();

            services.AddRouting();

            // Background services
            services.AddHostedService<ScoringService>();
            services.AddHostedService<WatchlistMonitor>();
        });

        webBuilder.Configure(app =>
        {
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/health", async (ITokenCacheService cache) =>
                {
                    var isRedisHealthy = await cache.IsHealthyAsync();
                    if (!isRedisHealthy)
                    {
                        return Results.Problem("Redis unhealthy", statusCode: 503);
                    }
                    
                    var stats = await cache.GetStatsAsync();
                    return Results.Ok(new 
                    { 
                        status = "healthy", 
                        redis = "connected",
                        bitquery = "enabled",
                        watchlist = stats.WatchlistCount,
                        blacklist = stats.BlacklistCount
                    });
                });
            });
        });
    })
    .Build();

await builder.RunAsync();

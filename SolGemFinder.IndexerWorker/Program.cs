using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolGemFinder.Shared.Services.BitQuery;
using SolGemFinder.IndexerWorker.Models;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.ConfigureServices((context, services) =>
        {
            var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Startup");

            var serviceBusConnection = System.Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION");
            if (string.IsNullOrWhiteSpace(serviceBusConnection))
            {
                logger.LogCritical("ServiceBus connection string is not configured. Set SERVICEBUS_CONNECTION environment variable.");
                throw new System.InvalidOperationException("ServiceBus connection string is required");
            }

            // Configure options
            services.Configure<BitQueryOptions>(context.Configuration.GetSection(BitQueryOptions.SectionName));
            services.Configure<IndexerServiceOptions>(context.Configuration.GetSection(IndexerServiceOptions.SectionName));

            // HTTP Client for BitQuery GraphQL API
            services.AddHttpClient("BitQuery")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });

            // Azure Service Bus
            services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            services.AddSingleton<IndexerDiscoveryPublisher>();
            services.AddSingleton<IIndexerPublisher>(sp => sp.GetRequiredService<IndexerDiscoveryPublisher>());

            // BitQuery PumpFun Service (from Shared project)
            services.AddSingleton<IBitQueryPumpFunService, BitQueryPumpFunService>();

            // Background Service
            services.AddHostedService<IndexerService>();

            services.AddRouting();

            logger.LogInformation("? IndexerWorker configured with BitQuery integration (Shared)");
        });

        webBuilder.Configure(app =>
        {
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/health", () => Results.Ok("healthy"));
            });
        });
    })
    .Build();

await builder.RunAsync();

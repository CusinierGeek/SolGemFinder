using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolGemFinder.IndexerWorker.Models;
using SolGemFinder.IndexerWorker.Services;

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
            services.Configure<SolanaRadarOptions>(context.Configuration.GetSection(SolanaRadarOptions.SectionName));
            services.Configure<IndexerServiceOptions>(context.Configuration.GetSection(IndexerServiceOptions.SectionName));

            // Azure Service Bus
            services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            services.AddSingleton<IndexerDiscoveryPublisher>();
            services.AddSingleton<IIndexerPublisher>(sp => sp.GetRequiredService<IndexerDiscoveryPublisher>());

            // Solana Radar Service
            services.AddSingleton<ISolanaRadar, SolanaRadar>();

            // Background Service
            services.AddHostedService<IndexerService>();

            services.AddRouting();
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

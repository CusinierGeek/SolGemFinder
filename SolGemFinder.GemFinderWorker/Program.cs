using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.ConfigureServices((context, services) =>
        {
            IConfiguration config = context.Configuration;
            var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Startup");

            // Read connection string from config or environment
            var serviceBusConnection = config.GetValue<string>("ServiceBus:ConnectionString") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION");

            if (string.IsNullOrWhiteSpace(serviceBusConnection))
            {
                logger.LogCritical("ServiceBus connection string is not configured. Set ServiceBus:ConnectionString in appsettings or SERVICEBUS_CONNECTION environment variable.");
                throw new System.InvalidOperationException("ServiceBus connection string is required");
            }

            // Register Service Bus client and publisher (no entity creation here)
            services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            services.AddSingleton<ServiceBusDiscoveryPublisher>();
            services.AddSingleton<IDiscoveryPublisher>(sp => sp.GetRequiredService<ServiceBusDiscoveryPublisher>());

            services.AddRouting();

            services.AddHostedService<GemFinderService>();
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

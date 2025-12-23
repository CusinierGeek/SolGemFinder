using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

            services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            services.AddSingleton<ServiceBusDiscoveryConsumer>();
            services.AddSingleton<IDiscoveryConsumer>(sp => sp.GetRequiredService<ServiceBusDiscoveryConsumer>());

            services.AddHostedService<TradingService>();

            services.AddRouting();
        });

        webBuilder.Configure(app =>
        {
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                // health
                endpoints.MapGet("/health", () => Results.Ok("healthy"));
            });
        });
    })
    .Build();

await builder.RunAsync();

using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
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

            var serviceBusConnection = config.GetValue<string>("ServiceBus:ConnectionString") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION");
            if (string.IsNullOrWhiteSpace(serviceBusConnection))
            {
                logger.LogCritical("ServiceBus connection string is not configured. Set ServiceBus:ConnectionString in appsettings or SERVICEBUS_CONNECTION environment variable.");
                throw new System.InvalidOperationException("ServiceBus connection string is required");
            }

            try
            {
                var adminClient = new ServiceBusAdministrationClient(serviceBusConnection);
                var topicName = config.GetValue<string>("ServiceBus:Topic") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_TOPIC") ?? "discoveries";
                var subscriptionName = config.GetValue<string>("ServiceBus:Subscription") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_SUBSCRIPTION") ?? "trading-sub";

                if (!adminClient.TopicExistsAsync(topicName).GetAwaiter().GetResult())
                {
                    logger.LogInformation("Creating topic {Topic}", topicName);
                    adminClient.CreateTopicAsync(topicName).GetAwaiter().GetResult();
                }

                if (!adminClient.SubscriptionExistsAsync(topicName, subscriptionName).GetAwaiter().GetResult())
                {
                    logger.LogInformation("Creating subscription {Subscription} for topic {Topic}", subscriptionName, topicName);
                    adminClient.CreateSubscriptionAsync(new CreateSubscriptionOptions(topicName, subscriptionName)).GetAwaiter().GetResult();
                }
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Failed to ensure Service Bus entities");
                throw;
            }

            services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            services.AddSingleton<ServiceBusDiscoveryConsumer>();
            services.AddSingleton<IDiscoveryConsumer>(sp => sp.GetRequiredService<ServiceBusDiscoveryConsumer>());

            services.AddHttpClient();

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

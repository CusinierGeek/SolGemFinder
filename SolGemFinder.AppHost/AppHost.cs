using Microsoft.Extensions.Configuration;

// Load AppHost configuration and expose ServiceBus settings as environment variables
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var sbConn = config["ServiceBus:ConnectionString"];
var sbTopic = config["ServiceBus:Topic"];
var sbSub = config["ServiceBus:Subscription"];

if (!string.IsNullOrWhiteSpace(sbConn))
    Environment.SetEnvironmentVariable("SERVICEBUS_CONNECTION", sbConn);
if (!string.IsNullOrWhiteSpace(sbTopic))
    Environment.SetEnvironmentVariable("SERVICEBUS_TOPIC", sbTopic);
if (!string.IsNullOrWhiteSpace(sbSub))
    Environment.SetEnvironmentVariable("SERVICEBUS_SUBSCRIPTION", sbSub);

var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.SolGemFinder_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.SolGemFinder_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

// Gem finder worker - discovers potential tokens (architecture only)
var gemFinder = builder.AddProject<Projects.SolGemFinder_GemFinderWorker>("gemfinder")
    .WithHttpHealthCheck("/health")
    .WithReference(apiService);

// Trading worker - consumes discoveries and executes trades (architecture only)
builder.AddProject<Projects.SolGemFinder_TradingWorker>("trading")
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WithReference(gemFinder)
    .WaitFor(apiService)
    .WaitFor(gemFinder);

builder.Build().Run();

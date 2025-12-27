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

// Add Redis with persistent volume for watchlist survival
var redis = builder.AddRedis("redis")
    .WithDataVolume("solgemfinder-redis-data")  // Persistent volume
    .WithLifetime(ContainerLifetime.Persistent) // Keep container between runs
    .WithRedisCommander();

var apiService = builder.AddProject<Projects.SolGemFinder_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.SolGemFinder_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

// Scoring worker (Detection Worker) - monitors watchlist and scores tokens
var scoringWorker = builder.AddProject<Projects.SolGemFinder_ScoringWorker>("ScoringWorker")
    .WithHttpHealthCheck("/health")
    .WithReference(redis)  // Redis for watchlist cache
    .WithReference(apiService);

// Trading worker - consumes discoveries and executes trades
builder.AddProject<Projects.SolGemFinder_TradingWorker>("trading")
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WithReference(scoringWorker)
    .WaitFor(apiService)
    .WaitFor(scoringWorker);

// Indexer worker - listens to pump.fun and publishes discoveries
builder.AddProject<Projects.SolGemFinder_IndexerWorker>("indexer")
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();

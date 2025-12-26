using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolGemFinder.Shared.Dtos;

public class ScoringConsumer
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ScoringConsumer> _logger;
    private ServiceBusProcessor? _processor;
    private readonly string _topic;
    private readonly string _subscription;
    private readonly ServiceBusDiscoveryPublisher _publisher;

    public ScoringConsumer(ServiceBusClient client, IConfiguration config, ILogger<ScoringConsumer> logger, ServiceBusDiscoveryPublisher publisher)
    {
        _client = client;
        _logger = logger;
        _publisher = publisher;
        _topic = config.GetValue<string>("ServiceBus:Topic") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_TOPIC") ?? "discoveries";
        _subscription = config.GetValue<string>("ServiceBus:GemFinderSubscription") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_GEMFINDER_SUBSCRIPTION") ?? "gemfinder-sub";
    }

    public void Start()
    {
        if (_processor != null) return;

        _processor = _client.CreateProcessor(_topic, _subscription, new ServiceBusProcessorOptions { AutoCompleteMessages = false });
        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;
        _processor.StartProcessingAsync();
    }

    public async Task StopAsync()
    {
        if (_processor != null)
        {
            await _processor.StopProcessingAsync();
            await _processor.DisposeAsync();
            _processor = null;
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var body = args.Message.Body.ToString();
        try
        {
            var dto = JsonSerializer.Deserialize<DiscoveryDto>(body);
            _logger.LogInformation("GemFinder received discovery {Id} from {Source} with history: {History}", dto?.Id, dto?.Source, dto?.History);

            if (dto is not null)
            {
                // append to history
                var newHistory = (dto.History ?? "") + ";GemFinder:scanned";
                var newDto = dto with { Stage = "gemfinder", History = newHistory };

                _logger.LogInformation("GemFinder publishing updated discovery {Id} with history: {History}", newDto.Id, newDto.History);
                await _publisher.PublishAsync(newDto);
            }

            await args.CompleteMessageAsync(args.Message);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error handling discovery in GemFinder");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "ServiceBus error in GemFinderConsumer");
        return Task.CompletedTask;
    }
}

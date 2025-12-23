using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public interface IDiscoveryConsumer
{
    Task HandleAsync(string json);
}

public class ServiceBusDiscoveryConsumer : IDiscoveryConsumer
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusDiscoveryConsumer> _logger;
    private ServiceBusProcessor? _processor;
    private readonly string _topic;
    private readonly string _subscription;

    public ServiceBusDiscoveryConsumer(ServiceBusClient client, IConfiguration config, ILogger<ServiceBusDiscoveryConsumer> logger)
    {
        _client = client;
        _logger = logger;
        _topic = config.GetValue<string>("ServiceBus:Topic") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_TOPIC") ?? "discoveries";
        _subscription = config.GetValue<string>("ServiceBus:Subscription") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_SUBSCRIPTION") ?? "trading-sub";
        // Do not start the processor here; TradingService controls lifecycle.
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
            await HandleAsync(body);
            await args.CompleteMessageAsync(args.Message);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "ServiceBus error");
        return Task.CompletedTask;
    }

    public Task HandleAsync(string json)
    {
        _logger.LogInformation("ServiceBus received discovery: {Json}", json);
        return Task.CompletedTask;
    }
}

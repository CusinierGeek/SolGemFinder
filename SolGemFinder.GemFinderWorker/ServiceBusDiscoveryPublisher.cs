using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolGemFinder.Shared.Dtos;

public class ServiceBusDiscoveryPublisher : IDiscoveryPublisher
{
    private readonly ServiceBusClient _client;
    private readonly string _topic;
    private readonly ILogger<ServiceBusDiscoveryPublisher> _logger;

    public ServiceBusDiscoveryPublisher(ServiceBusClient client, IConfiguration config, ILogger<ServiceBusDiscoveryPublisher> logger)
    {
        _client = client;
        _logger = logger;
        _topic = config.GetValue<string>("ServiceBus:Topic") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_TOPIC") ?? "discoveries";
    }

    public async Task PublishAsync<T>(T payload, CancellationToken ct = default)
    {
        var sender = _client.CreateSender(_topic);
        var json = JsonSerializer.Serialize(payload);
        var msg = new ServiceBusMessage(json);

        // set MessageId for traceability
        msg.MessageId = System.Guid.NewGuid().ToString();

        // if payload is DiscoveryDto, set application properties used by subscription filters
        if (payload is DiscoveryDto d)
        {
            if (!string.IsNullOrEmpty(d.Stage))
                msg.ApplicationProperties["stage"] = d.Stage;
            if (!string.IsNullOrEmpty(d.Source))
                msg.ApplicationProperties["source"] = d.Source;

            // include id and history as properties for easier viewing
            msg.ApplicationProperties["id"] = d.Id.ToString();
            if (!string.IsNullOrEmpty(d.History))
                msg.ApplicationProperties["history"] = d.History;

            _logger.LogInformation("Publishing DiscoveryDto {Id} stage={Stage} history={History}", d.Id, d.Stage, d.History);
        }
        else
        {
            _logger.LogInformation("Publishing message to topic {Topic}", _topic);
        }

        await sender.SendMessageAsync(msg, ct);
    }
}

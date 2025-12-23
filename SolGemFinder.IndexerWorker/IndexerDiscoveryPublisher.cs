using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using SolGemFinder.Shared.Dtos;

public class IndexerDiscoveryPublisher : IIndexerPublisher
{
    private readonly ServiceBusClient _client;
    private readonly string _topic;

    public IndexerDiscoveryPublisher(ServiceBusClient client, IConfiguration config)
    {
        _client = client;
        _topic = config.GetValue<string>("ServiceBus:Topic") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_TOPIC") ?? "discoveries";
    }

    public async Task PublishAsync<T>(T payload, CancellationToken ct = default)
    {
        var sender = _client.CreateSender(_topic);
        var json = JsonSerializer.Serialize(payload);
        var msg = new ServiceBusMessage(json);
        // set stage property for downstream filters
        if (payload is DiscoveryDto d && !string.IsNullOrEmpty(d.Stage))
        {
            msg.ApplicationProperties["stage"] = d.Stage;
            msg.ApplicationProperties["source"] = d.Source;
        }
        await sender.SendMessageAsync(msg, ct);
    }
}

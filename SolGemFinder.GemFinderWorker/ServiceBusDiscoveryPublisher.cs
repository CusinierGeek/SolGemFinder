using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;

public class ServiceBusDiscoveryPublisher : IDiscoveryPublisher
{
    private readonly ServiceBusClient _client;
    private readonly string _topic;

    public ServiceBusDiscoveryPublisher(ServiceBusClient client, IConfiguration config)
    {
        _client = client;
        _topic = config.GetValue<string>("ServiceBus:Topic") ?? System.Environment.GetEnvironmentVariable("SERVICEBUS_TOPIC") ?? "discoveries";
    }

    public async Task PublishAsync<T>(T payload, CancellationToken ct = default)
    {
        var sender = _client.CreateSender(_topic);
        var json = JsonSerializer.Serialize(payload);
        var msg = new ServiceBusMessage(json);
        await sender.SendMessageAsync(msg, ct);
    }
}

using Microsoft.Extensions.Logging;
using System.Text.Json;

public class DiscoveryConsumer
{
    private readonly ILogger<DiscoveryConsumer> _logger;

    public DiscoveryConsumer(ILogger<DiscoveryConsumer> logger)
    {
        _logger = logger;
    }

    public void HandleMessage(object message)
    {
        var json = JsonSerializer.Serialize(message);
        _logger.LogInformation("Handled discovery message: {Body}", json);
    }
}

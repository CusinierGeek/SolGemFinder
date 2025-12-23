using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("discoveries")]
public class DiscoveryController : ControllerBase
{
    private readonly ILogger<DiscoveryController> _logger;
    private readonly IDiscoveryConsumer _consumer;

    public DiscoveryController(ILogger<DiscoveryController> logger, IDiscoveryConsumer consumer)
    {
        _logger = logger;
        _consumer = consumer;
    }

    [HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromBody] object payload)
    {
        _logger.LogInformation("Received discovery POST: {Payload}", payload);
        await _consumer.HandleAsync(System.Text.Json.JsonSerializer.Serialize(payload));
        return Accepted();
    }
}

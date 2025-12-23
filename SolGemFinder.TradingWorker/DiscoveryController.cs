using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SolGemFinder.Shared.Dtos;
using System.Text.Json;

[ApiController]
[Route("discoveries")]
public class DiscoveryController : ControllerBase
{
    private readonly ILogger<DiscoveryController> _logger;

    public DiscoveryController(ILogger<DiscoveryController> logger)
    {
        _logger = logger;
    }

    [HttpPost]
    [Route("")]
    public async Task<IActionResult> Post([FromBody] object payload)
    {
        _logger.LogInformation("Received discovery POST: {Payload}", payload);
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var dto = JsonSerializer.Deserialize<DiscoveryDto>(json);
            _logger.LogInformation("Trading received final discovery {Id} from {Source} with history: {History}", dto?.Id, dto?.Source, dto?.History);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse discovery payload");
        }

        return Accepted();
    }
}

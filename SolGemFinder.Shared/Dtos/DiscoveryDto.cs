namespace SolGemFinder.Shared.Dtos;

public record DiscoveryDto
{
    public required System.Guid Id { get; init; }
    public required string Source { get; init; }
    public required System.DateTime Timestamp { get; init; }
    public string? Stage { get; init; }
    public string? History { get; init; }
}

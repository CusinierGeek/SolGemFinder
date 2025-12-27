using SolGemFinder.Shared.Services.BitQuery;

namespace SolGemFinder.IndexerWorker.Models;

public class IndexerServiceOptions
{
    public const string SectionName = "IndexerService";

    public int PollingIntervalMs { get; set; } = 30000; // 30 seconds (less frequent than before)

    public TokenDiscoveryStrategy DiscoveryStrategy { get; set; } = TokenDiscoveryStrategy.AboutToGraduate;
}

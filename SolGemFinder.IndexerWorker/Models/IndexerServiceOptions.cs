namespace SolGemFinder.IndexerWorker.Models;

public class IndexerServiceOptions
{
    public const string SectionName = "IndexerService";

    public int PollingIntervalMs { get; set; } = 5000;
}

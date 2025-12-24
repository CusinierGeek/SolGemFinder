namespace SolGemFinder.IndexerWorker.Models;

public class SolanaRadarOptions
{
    public const string SectionName = "SolanaRadar";

    public string RpcUrl { get; set; } = string.Empty;
    public string ProgramId { get; set; } = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
    public int InitialSlotOffset { get; set; } = 100;
    public int SignatureLimit { get; set; } = 50;
}

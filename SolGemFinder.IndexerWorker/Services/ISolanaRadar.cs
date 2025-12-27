using SolGemFinder.IndexerWorker.Models;
using SolGemFinder.Shared.Services.BitQuery;

namespace SolGemFinder.IndexerWorker.Services;

/// <summary>
/// Interface for scanning the Solana blockchain for new Pump.fun token creations and trades.
/// </summary>
public interface ISolanaRadar
{
    /// <summary>
    /// Scans for new Pump.fun tokens by monitoring program transactions.
    /// </summary>
    /// <param name="ct">Cancellation token for stopping the scan operation.</param>
    /// <returns>A list of discovered token information.</returns>
    Task<List<PumpFunTokenInfo>> ScanForNewTokensAsync(CancellationToken ct = default);
}

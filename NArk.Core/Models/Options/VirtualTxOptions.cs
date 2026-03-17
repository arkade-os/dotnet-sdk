using NArk.Abstractions.VirtualTxs;

namespace NArk.Core.Models.Options;

/// <summary>
/// Configuration options for virtual tx fetching and storage.
/// </summary>
public class VirtualTxOptions
{
    /// <summary>
    /// Default mode for virtual tx storage. Full stores raw tx hex immediately;
    /// Lite stores only txids and fetches hex on demand for exit.
    /// </summary>
    public VirtualTxMode DefaultMode { get; set; } = VirtualTxMode.Full;

    /// <summary>
    /// Minimum VTXO amount (in sats) worth fetching exit data for.
    /// VTXOs below this threshold are skipped to avoid storing exit data
    /// that would cost more in fees than the VTXO is worth.
    /// </summary>
    public ulong MinExitWorthAmount { get; set; } = 1000;
}

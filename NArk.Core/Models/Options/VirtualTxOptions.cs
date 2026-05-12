using NArk.Abstractions.VirtualTxs;

namespace NArk.Core.Models.Options;

/// <summary>
/// Configuration options for virtual tx fetching and storage.
/// </summary>
public class VirtualTxOptions
{
    /// <summary>
    /// Default mode for virtual tx storage. Full stores raw tx hex immediately;
    /// Lite stores only txids + expiry and defers hex fetch until the first
    /// <c>UnilateralExitService.StartExitAsync</c> call for that VTXO.
    /// <para>
    /// Lite is the default because the common case never exits unilaterally —
    /// most VTXOs settle into the next batch or get spent off-chain, and
    /// storing hex for all of them is pure cost. Switch to Full if your
    /// application has strict offline-exit requirements (e.g. it must be able
    /// to broadcast the exit chain without an indexer round-trip).
    /// </para>
    /// </summary>
    public VirtualTxMode DefaultMode { get; set; } = VirtualTxMode.Lite;

    /// <summary>
    /// Minimum VTXO amount (in sats) worth fetching exit data for.
    /// VTXOs below this threshold are skipped to avoid storing exit data
    /// that would cost more in fees than the VTXO is worth.
    /// </summary>
    public ulong MinExitWorthAmount { get; set; } = 1000;
}

using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;

namespace NArk.Abstractions.Services;

/// <summary>
/// Allows host applications to override the default onchain sweep behavior
/// for expired boarding/unrolled UTXOs.
/// </summary>
public interface IOnchainSweepHandler
{
    /// <summary>
    /// Called when an unrolled/boarding UTXO's CSV has expired and needs sweeping.
    /// Return true if handled (sweep was performed), false to use default behavior.
    /// </summary>
    /// <param name="walletId">The wallet identifier that owns the UTXO.</param>
    /// <param name="vtxo">The expired VTXO.</param>
    /// <param name="contract">The contract entity associated with the VTXO.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> HandleExpiredUtxoAsync(
        string walletId,
        ArkVtxo vtxo,
        ArkContractEntity contract,
        CancellationToken cancellationToken);
}

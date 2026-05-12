using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Fallback <see cref="IBitcoinBlockchain"/> for the WASM sample when no
/// explorer is configured. Returns "now" + height 0 for chain time so VTXOs
/// don't show as expired; everything else throws <see cref="NotSupportedException"/>
/// — the sample doesn't drive broadcast/UTXO/exit paths in this configuration.
/// </summary>
public class FallbackChainTimeProvider : IBitcoinBlockchain
{
    public Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
        => Task.FromResult(new TimeHeight(DateTimeOffset.UtcNow, 0));

    public Task<IReadOnlyList<BoardingUtxo>> GetUtxosAsync(string address, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FallbackChainTimeProvider: no explorer configured for UTXO lookup.");

    public Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FallbackChainTimeProvider: no explorer configured for broadcast.");

    public Task<bool> BroadcastPackageAsync(Transaction parent, Transaction child, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FallbackChainTimeProvider: no explorer configured for package broadcast.");

    public Task<TxStatus> GetTxStatusAsync(uint256 txid, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FallbackChainTimeProvider: no explorer configured for tx status.");

    public Task<FeeRate> EstimateFeeRateAsync(int confirmTarget = 6, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("FallbackChainTimeProvider: no explorer configured for fee estimation.");
}

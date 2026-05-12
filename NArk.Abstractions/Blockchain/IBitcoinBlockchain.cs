using NBitcoin;

namespace NArk.Abstractions.Blockchain;

/// <summary>
/// Unified Bitcoin-blockchain backend: chain-time, address-indexed UTXO lookup,
/// transaction broadcast, tx-status, and fee estimation. Realistically every
/// concrete backend (NBXplorer, Esplora, Bitcoin Core RPC) is going to expose
/// some flavour of all of these, so the SDK takes them as one interface rather
/// than imposing the wiring tax of three split-by-responsibility abstractions.
/// <para>
/// Not every backend supports every method — Bitcoin Core RPC, for example,
/// has no native address-indexed UTXO API. Implementations should throw
/// <see cref="NotSupportedException"/> with a clear message for genuinely
/// unsupported operations. See per-impl docs for what each backend covers.
/// </para>
/// </summary>
public interface IBitcoinBlockchain
{
    /// <summary>
    /// Current chain time + block height. Used by the SDK for batch-expiry
    /// math, CSV-maturity checks, sweep eligibility, and similar timing
    /// decisions across spending, swaps, and unilateral exit.
    /// </summary>
    Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists confirmed + mempool UTXOs at a single on-chain address. Used to
    /// discover funds at boarding addresses (the on-chain entry point to a
    /// VTXO) and to drive HD-wallet recovery.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Backend has no address-indexed UTXO API (e.g. plain Bitcoin Core RPC
    /// without an external indexer). Use NBXplorer or Esplora when this
    /// capability is required.
    /// </exception>
    Task<IReadOnlyList<BoardingUtxo>> GetUtxosAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast a single transaction. Returns true when the broadcast was
    /// accepted (in mempool); false otherwise. Implementations should not
    /// throw on policy / consensus rejection — the rejection is observable
    /// and recoverable, but it isn't an exceptional condition for callers.
    /// </summary>
    Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast a 1p1c package (parent + CPFP child) via Bitcoin Core's
    /// <c>submitpackage</c>. Used by the unilateral-exit broadcaster to wrap
    /// each virtual tx with a fee-bearing child so it gets past TRUC policy.
    /// </summary>
    Task<bool> BroadcastPackageAsync(Transaction parent, Transaction child, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query whether a transaction has confirmed, is sitting in the mempool,
    /// or is unknown to the backend. The exit broadcaster + watchtower poll
    /// this to advance sessions from Broadcasting → AwaitingCsvDelay.
    /// </summary>
    Task<TxStatus> GetTxStatusAsync(uint256 txid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Estimate fee rate (sat/vB) for inclusion within
    /// <paramref name="confirmTarget"/> blocks. Used by CPFP child construction
    /// and the claim-tx builder.
    /// </summary>
    Task<FeeRate> EstimateFeeRateAsync(int confirmTarget = 6, CancellationToken cancellationToken = default);
}

/// <summary>
/// On-chain transaction status returned by <see cref="IBitcoinBlockchain.GetTxStatusAsync"/>.
/// </summary>
public record TxStatus(bool Confirmed, uint? BlockHeight, bool InMempool);

/// <summary>
/// On-chain UTXO at a boarding address, returned by
/// <see cref="IBitcoinBlockchain.GetUtxosAsync"/>.
/// </summary>
public record BoardingUtxo(
    string Txid,
    uint Vout,
    ulong Amount,
    bool Confirmed,
    long BlockHeight,
    long BlockTime);

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
    /// <para>
    /// <see cref="TimeHeight.Timestamp"/> must be the tip's <b>median time
    /// past</b> (BIP 113), not the tip block's own <c>nTime</c> — that's the
    /// clock consensus uses for time-based locks, so anything comparing a
    /// timelock against it (BIP-68 relative time locks, CLTV) would otherwise
    /// be off by up to a couple of hours.
    /// </para>
    /// </summary>
    Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default);

    /// <summary>
    /// Median time past (BIP 113) of the block at <paramref name="blockHeight"/>.
    /// Returns <c>null</c> when the backend has no block at that height.
    /// <para>
    /// Needed to evaluate BIP-68 <i>time-based</i> relative locks: a
    /// time-locked input matures once the spending block's MTP is at least
    /// the MTP of the block that confirmed the input plus the lock period.
    /// Block heights are meaningless for that comparison, so the unilateral
    /// exit path calls this whenever the operator advertises a time-based
    /// unilateral-exit delay (arkd's production default is 24 h).
    /// </para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Backend cannot resolve a historical block's median time past. Time-based
    /// relative locks cannot be evaluated against such a backend; use NBXplorer,
    /// Esplora, or Bitcoin Core RPC.
    /// </exception>
    Task<DateTimeOffset?> GetMedianTimePastAsync(uint blockHeight, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement GetMedianTimePastAsync, so BIP-68 time-based " +
            "relative locks (e.g. a time-based unilateral-exit delay) cannot be evaluated. " +
            "Use NBXplorerBlockchain, EsploraBlockchain, or RpcBlockchain.");

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

    /// <summary>
    /// Fetch a confirmed or mempool transaction in full, by txid. Returns
    /// <c>null</c> when the backend does not know the transaction.
    /// <para>
    /// Needed to carry a boarding or commitment transaction as a PSBT prevout
    /// field: the Arkade emulator requires the transaction behind every input
    /// of a submitted intent proof, and those two have no off-chain source, so
    /// arkd's indexer cannot serve them.
    /// </para>
    /// </summary>
    /// <param name="txid">The transaction id to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException">
    /// The backend cannot serve raw transactions. Default behaviour, so an
    /// existing implementation keeps compiling; every in-box backend overrides it.
    /// </exception>
    Task<Transaction?> GetRawTransactionAsync(uint256 txid, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} cannot fetch raw transactions by txid.");
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

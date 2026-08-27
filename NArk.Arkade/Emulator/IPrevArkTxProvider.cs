using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VirtualTxs;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Arkade.Emulator;

/// <summary>
/// Resolves raw Arkade transactions by txid, so a spend can attach them to its PSBT
/// inputs as <c>prevarktx</c> ark fields before the emulator sees it.
/// </summary>
/// <remarks>
/// <para>
/// Emulator <c>v0.0.7</c> (<c>validate checkpoints and prevouts</c>) requires the field
/// on <em>every</em> input of a submitted Arkade transaction or intent proof —
/// unconditionally, not only on the inputs whose ArkadeScript introspects a previous
/// output. A submission missing it is rejected with <c>missing prevout tx for input N</c>.
/// </para>
/// <para>
/// Only the previous transaction's <em>outputs</em> are read by the emulator (it checks
/// <c>txid</c>, reconciles value + scriptPubKey against the declared witness utxo, and
/// exposes the outputs to introspection opcodes). An unsigned copy therefore resolves
/// correctly — a txid is witness-excluded — which is why <see cref="PrevArkTxProvider"/>
/// can serve the PSBT's global transaction without waiting for signatures to propagate.
/// </para>
/// </remarks>
public interface IPrevArkTxProvider
{
    /// <summary>
    /// Resolves the raw transactions for the given txids.
    /// </summary>
    /// <param name="txids">Transaction ids to resolve; duplicates are collapsed.</param>
    /// <param name="network">Network to parse the fetched transactions against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The transactions that could be resolved, keyed by txid. Txids that could not be
    /// resolved are simply absent — callers decide whether a miss is fatal.
    /// </returns>
    Task<IReadOnlyDictionary<uint256, Transaction>> ResolveAsync(
        IReadOnlyCollection<uint256> txids,
        Network network,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IPrevArkTxProvider"/>: serves previous Arkade transactions from the
/// local virtual-tx store when present, then arkd's indexer (<c>GetVirtualTxs</c>), and
/// finally — when a blockchain backend is supplied — from chain.
/// </summary>
/// <remarks>
/// <para>
/// Storage-first matters because the store already holds the branch of every VTXO the
/// wallet has received, so a spend usually costs no extra indexer round-trip.
/// </para>
/// <para>
/// The on-chain step covers the parents the indexer cannot serve: a boarding UTXO, a
/// commitment transaction, or an unrolled coin re-entering from chain. Offchain Arkade
/// spends never need it (every input is a VTXO with a virtual parent), but intent proofs
/// routinely register boarding inputs, so wire <paramref name="blockchain"/> whenever
/// intent proofs go through the emulator.
/// </para>
/// </remarks>
public sealed class PrevArkTxProvider(
    IClientTransport transport,
    IVirtualTxStorage? virtualTxStorage = null,
    IBitcoinBlockchain? blockchain = null,
    ILogger<PrevArkTxProvider>? logger = null) : IPrevArkTxProvider
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<uint256, Transaction>> ResolveAsync(
        IReadOnlyCollection<uint256> txids,
        Network network,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(txids);
        ArgumentNullException.ThrowIfNull(network);

        var resolved = new Dictionary<uint256, Transaction>();
        var missing = new List<uint256>();

        foreach (var txid in txids.Distinct())
        {
            if (virtualTxStorage is not null)
            {
                var stored = await virtualTxStorage.GetVirtualTxAsync(txid.ToString(), cancellationToken);
                if (stored?.Hex is not null && TryParse(stored.Hex, network, out var storedTx)
                                            && storedTx!.GetHash() == txid)
                {
                    resolved[txid] = storedTx;
                    continue;
                }
            }

            missing.Add(txid);
        }

        if (missing.Count == 0)
            return resolved;

        // An indexer that rejects the whole batch over one txid it doesn't know must not
        // sink the parents the on-chain step could still serve, so a failure here is only
        // fatal when there is no on-chain step to fall through to.
        IReadOnlyList<string> hexes;
        try
        {
            hexes = await transport.GetVirtualTxsAsync(
                [.. missing.Select(t => t.ToString())], cancellationToken);
        }
        catch (Exception e) when (blockchain is not null && e is not OperationCanceledException)
        {
            logger?.LogDebug(e,
                "Indexer lookup of {Count} previous Arkade transaction(s) failed; falling back to chain",
                missing.Count);
            hexes = [];
        }

        // arkd's GetVirtualTxs (GetTxsWithTxids → SQL `WHERE id IN (...)`) returns hexes in DB
        // order, not request order, so pairing them positionally would attach one transaction's
        // body to another's txid. Key every result by the txid parsed out of the body itself,
        // then keep only the ones we actually asked for — that check is also what stops a
        // server from answering with a transaction of its choosing.
        var wanted = missing.ToHashSet();
        foreach (var hex in hexes)
        {
            if (!TryParse(hex, network, out var tx))
            {
                logger?.LogWarning("Skipping unparseable virtual tx while resolving prevarktx fields");
                continue;
            }

            var txid = tx!.GetHash();
            if (wanted.Contains(txid))
                resolved[txid] = tx;
        }

        if (blockchain is null)
            return resolved;

        // Boarding and commitment parents have no off-chain body. GetRawTransactionAsync
        // already verifies the returned transaction hashes to the requested txid.
        foreach (var txid in missing.Where(t => !resolved.ContainsKey(t)))
        {
            try
            {
                if (await blockchain.GetRawTransactionAsync(txid, cancellationToken) is { } onchain)
                    resolved[txid] = onchain;
            }
            catch (NotSupportedException e)
            {
                // The backend has no raw-tx endpoint at all, so every remaining txid would
                // fail identically. Say so once and loudly: swallowed per-txid at debug
                // level, the caller's "could not be resolved" error names the symptom and
                // hides the cause, which is a missing override on a custom backend.
                logger?.LogWarning(e,
                    "{Backend} cannot serve raw transactions, so previous transactions with an " +
                    "on-chain parent cannot be resolved. Override GetRawTransactionAsync on it to " +
                    "support boarding and commitment parents.",
                    blockchain.GetType().Name);
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A miss is reported by the caller alongside every other unresolved txid.
                logger?.LogDebug(e, "On-chain lookup of previous transaction {Txid} failed", txid);
            }
        }

        return resolved;
    }

    // arkd serves virtual txs as PSBTs. Only the global (unsigned) transaction is needed:
    // the emulator reads the outputs and the txid, both of which are witness-independent.
    private static bool TryParse(string hex, Network network, out Transaction? tx)
    {
        try
        {
            tx = PSBT.Parse(hex, network).GetGlobalTransaction();
            return true;
        }
        catch (Exception)
        {
            tx = null;
            return false;
        }
    }
}

using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Lightning;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.ArkadeIntents.Recovery;

/// <summary>What the chain says became of a swap lockup.</summary>
public enum LockupFate
{
    /// <summary>
    /// Nothing was learned. Never an answer.
    /// </summary>
    /// <remarks>
    /// No outputs visible, or one spent by a transaction the indexer could not produce. Indexer lag
    /// and a lockup that was never funded look identical from here, and neither is evidence of
    /// anything — which is why this is distinct from <see cref="Returned"/> rather than folded into
    /// it. Reading silence as a refund is how a swap that settled gets reported as one that did not.
    /// </remarks>
    Unknown,

    /// <summary>At least one output is still unspent and still reachable. Not over.</summary>
    Open,

    /// <summary>
    /// Spent by a witness carrying a preimage that hashes to this swap's payment hash.
    /// </summary>
    /// <remarks>
    /// Only a claim leaf can reveal one, and the only legitimate way the counterparty obtains it is
    /// by completing its side — so this is proof rather than inference.
    /// </remarks>
    Claimed,

    /// <summary>
    /// Fully spent, and nothing that spent it revealed a matching preimage — so it came back to us.
    /// </summary>
    /// <remarks>
    /// Decidable without asking anyone: every leaf that is not a claim is a refund. The covenant's
    /// non-interactive refund is pinned to our own address, and the other refund leaves all need our
    /// own signature. "Spent, but not by a hash-verified claim" therefore means the money went back.
    /// </remarks>
    Returned,

    /// <summary>
    /// At least one output was unilaterally exited: it now sits on-chain under the same script,
    /// where no off-chain claim or refund can reach it.
    /// </summary>
    /// <remarks>
    /// Not terminal and not a loss — the leaves are unchanged, so finishing the unroll and spending
    /// on-chain still ends the swap either way. It outranks <see cref="Open"/> deliberately: an
    /// output that is "still unspent" but unreachable off-chain is not a swap that is merely
    /// running, and reporting it as one is what leaves a caller waiting for a spend that cannot come.
    /// </remarks>
    Exited,

    /// <summary>
    /// At least one unspent output was swept by the operator after the lockup expired.
    /// </summary>
    /// <remarks>
    /// The covenant's leaves can no longer spend it; recovering it is a separate operation on the
    /// wallet's own recovery path. Ranked beside <see cref="Exited"/> for the same reason — the
    /// money is somewhere a refund cannot reach.
    /// </remarks>
    Swept,
}

/// <summary>What a look at a lockup found.</summary>
/// <param name="Fate">The verdict.</param>
/// <param name="Preimage">
/// The secret the spend revealed, on <see cref="LockupFate.Claimed"/> only. Proof, not a hint.
/// </param>
/// <param name="Stuck">
/// The outputs that are out of the covenant's reach, on <see cref="LockupFate.Exited"/> and
/// <see cref="LockupFate.Swept"/>. Named rather than counted, because a caller has to act on them
/// individually.
/// </param>
public sealed record LockupFateResult(
    LockupFate Fate,
    byte[]? Preimage = null,
    IReadOnlyList<OutPoint>? Stuck = null)
{
    /// <summary>True once nothing further will happen to this lockup on its own.</summary>
    public bool IsResolved => Fate is LockupFate.Claimed or LockupFate.Returned;
}

/// <summary>
/// Deciding from chain data alone whether a swap lockup settled, came back, or is still live.
/// </summary>
/// <remarks>
/// <para>
/// Corridor-neutral on purpose: all four HTLC corridors settle into the same covenant, so what a
/// spend of it means does not depend on which one negotiated it. The Lightning legs, the off-board
/// and the on-board all read the same way here.
/// </para>
/// <para>
/// Nothing is taken on the counterparty's word. The solver's RFQ status is a convenience and this is
/// the money path — a funded lockup is equally observable whether or not the solver answers, and a
/// solver that would misreport a settlement is exactly the one whose answer matters least.
/// </para>
/// </remarks>
public static class LockupFateReader
{
    /// <summary>
    /// Read a lockup's fate.
    /// </summary>
    /// <param name="transport">Where spending transactions are fetched from.</param>
    /// <param name="vtxoStorage">The chain view of the lockup's outputs.</param>
    /// <param name="swapPkScript">The lockup's scriptPubKey, hex.</param>
    /// <param name="paymentHashHex">This swap's payment hash, big-endian hex.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The verdict, with its proof or its stuck outputs.</returns>
    /// <remarks>
    /// The unreachable cases are checked before any witness is read, so a lockup with one exited
    /// output and one claimed sibling reports <see cref="LockupFate.Exited"/> rather than a verdict
    /// that would suggest there is nothing left to do.
    /// </remarks>
    public static async Task<LockupFateResult> ReadAsync(
        IClientTransport transport,
        IVtxoStorage vtxoStorage,
        string swapPkScript,
        string paymentHashHex,
        CancellationToken cancellationToken = default)
    {
        var vtxos = await vtxoStorage.GetVtxos(
            scripts: [swapPkScript], includeSpent: true, cancellationToken: cancellationToken);

        if (vtxos.Count == 0) return new LockupFateResult(LockupFate.Unknown);

        var unspent = vtxos.Where(v => !v.IsSpent()).ToList();

        // Out of reach first. An output that is unspent and unreachable is not a swap still running,
        // and saying "open" about it leaves the caller waiting for a spend that cannot come.
        if (unspent.Where(v => v.Unrolled && !v.Swept).ToList() is { Count: > 0 } exited)
        {
            return new LockupFateResult(LockupFate.Exited, Stuck: exited.Select(Outpoint).ToList());
        }
        if (unspent.Where(v => v.Swept).ToList() is { Count: > 0 } swept)
        {
            return new LockupFateResult(LockupFate.Swept, Stuck: swept.Select(Outpoint).ToList());
        }
        if (unspent.Count > 0) return new LockupFateResult(LockupFate.Open);

        // Everything is spent. Whether that was a claim is decided by a preimage that hashes to this
        // swap's own payment hash — a witness of the right SHAPE is not proof, and reading it as one
        // would report a refunded swap as settled, which is the fact a trader relies on most.
        var sawASpend = false;
        foreach (var vtxo in vtxos)
        {
            var spender = SpenderOf(vtxo);
            if (spender is null) continue;

            // Fetched here as well as inside the preimage search, because the two silences differ
            // and only one of them is an answer. `FindAsync` returns null both for "carried no
            // preimage" and "could not be fetched" — fine for a status nudge, not fine for a
            // VERDICT, since `Returned` would then be pronounced over an indexer that was merely
            // down. Seeing the transaction at all is what earns the right to conclude anything.
            var seen = await SpendIsVisibleAsync(transport, spender, cancellationToken);
            if (!seen) continue;
            sawASpend = true;

            var preimage = await SwapPreimageReader.FindAsync(
                transport, Outpoint(vtxo), spender, paymentHashHex, cancellationToken);
            if (preimage is not null) return new LockupFateResult(LockupFate.Claimed, preimage);
        }

        // Spent by something we could actually read, and nothing proved a claim. Every non-claim
        // leaf returns the money to us, so this is the refund.
        return sawASpend
            ? new LockupFateResult(LockupFate.Returned)
            : new LockupFateResult(LockupFate.Unknown);
    }

    /// <summary>The transaction that spent this output, by the same rule <see cref="ArkVtxo.IsSpent"/> uses.</summary>
    /// <remarks>
    /// Written to match <see cref="ArkVtxo.IsSpent"/> rather than as a <c>??</c> chain. That method
    /// treats an empty string as absent, so <c>??</c> would hand back an empty
    /// <see cref="ArkVtxo.SpentByTransactionId"/> in preference to a real settlement id — and the
    /// spend would then be skipped, which turns a <see cref="LockupFate.Returned"/> verdict into
    /// <see cref="LockupFate.Unknown"/> over a lockup that plainly came back.
    /// <para>
    /// <see cref="ArkVtxo.ArkTxid"/> is deliberately not consulted. This runs only once every output
    /// is spent, so one of the two ids above is always present; offering a third suggests it fills a
    /// gap that the type shows it cannot.
    /// </para>
    /// </remarks>
    private static string? SpenderOf(ArkVtxo vtxo) =>
        !string.IsNullOrEmpty(vtxo.SpentByTransactionId) ? vtxo.SpentByTransactionId
        : !string.IsNullOrEmpty(vtxo.SettledByTransactionId) ? vtxo.SettledByTransactionId
        : null;

    /// <summary>Whether the indexer can actually produce the transaction that spent this lockup.</summary>
    private static async Task<bool> SpendIsVisibleAsync(
        IClientTransport transport, string txid, CancellationToken cancellationToken)
    {
        try
        {
            return (await transport.GetVirtualTxsAsync([txid], cancellationToken)).Count > 0;
        }
        catch (Exception)
        {
            // An outage is not a fact about the swap.
            return false;
        }
    }

    private static OutPoint Outpoint(ArkVtxo vtxo) =>
        new(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);
}

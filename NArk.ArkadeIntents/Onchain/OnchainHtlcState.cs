using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>Where an L1 HTLC stands, as the chain reports it.</summary>
public enum OnchainHtlcPhase
{
    /// <summary>Nothing has arrived at the address.</summary>
    Unfunded,

    /// <summary>Funded, but short of the confirmations the swap was quoted at.</summary>
    AwaitingConfirmations,

    /// <summary>Funded, confirmed, and there is still room to claim before the refund leaf opens.</summary>
    Claimable,

    /// <summary>
    /// The refund leaf has matured, which means <b>the claim window is closed</b>.
    /// </summary>
    /// <remarks>
    /// A recovery caller reading this must not try to claim. Reaching it on a swap you expected to
    /// claim means the claim was <em>missed</em>, not that it is still available — the correct move
    /// is the refund on whichever rail is yours, and on an off-board that is the Arkade covenant
    /// rather than anything here.
    /// </remarks>
    Refundable,

    /// <summary>Nothing is left at the address: somebody already took it, by one leaf or the other.</summary>
    /// <remarks>
    /// Which leaf is not decidable from the absence alone. On an on-board a settled HTLC is the
    /// solver collecting with the preimage our own claim published, which is the ordinary end;
    /// <see cref="OnchainHtlcState.ExtractPreimage"/> against the spending transaction is what turns
    /// that from an inference into a proof.
    /// </remarks>
    Settled,
}

/// <summary>What a look at an L1 HTLC found.</summary>
/// <param name="Phase">Where it stands.</param>
/// <param name="Utxos">The outputs at the address that count toward <paramref name="Phase"/>.</param>
/// <param name="TotalSats">What those outputs hold, in sats.</param>
public sealed record OnchainHtlcStatus(
    OnchainHtlcPhase Phase, IReadOnlyList<BoardingUtxo> Utxos, ulong TotalSats);

/// <summary>
/// Reading an L1 HTLC's state back off the chain, for a client that no longer knows it.
/// </summary>
/// <remarks>
/// <para>
/// The corridor's normal path never needs this: it drives forward from a row it wrote itself. This
/// is for the case where that row is gone or stale — a restored wallet, a process that was down
/// across the window, an operator asking what actually happened. The distinction matters because the
/// answers differ in kind: the drive path asks "may I act yet", and recovery asks "what is true".
/// </para>
/// <para>
/// Everything here is derived from the chain and the contract, never from a counterparty's account
/// of either.
/// </para>
/// </remarks>
public static class OnchainHtlcState
{
    /// <summary>
    /// Classify an HTLC from the chain.
    /// </summary>
    /// <param name="blockchain">Where to read outputs and the tip from.</param>
    /// <param name="htlc">The HTLC to look at.</param>
    /// <param name="minConfirmations">The count this swap was quoted at.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The phase, and the outputs behind it.</returns>
    /// <remarks>
    /// Maturity is judged against the tip's <b>median time past</b>, which is the clock consensus
    /// applies to CLTV and which trails wall clock by roughly an hour. Classifying against a local
    /// clock would report <see cref="OnchainHtlcPhase.Refundable"/> for up to that long before a
    /// refund would actually be accepted — and, worse in the other direction, would call a window
    /// closed while a claim could still have landed.
    /// </remarks>
    public static async Task<OnchainHtlcStatus> ClassifyAsync(
        IBitcoinBlockchain blockchain,
        OnchainHtlc htlc,
        int minConfirmations,
        CancellationToken cancellationToken = default)
    {
        var utxos = await blockchain.GetUtxosAsync(htlc.Address.ToString(), cancellationToken);
        if (utxos.Count == 0)
        {
            // Unfunded and already-spent are the same emptiness from an address query. The caller
            // knows which it expected; what it needs from here is that nothing is claimable.
            return new OnchainHtlcStatus(OnchainHtlcPhase.Settled, [], 0);
        }

        var chain = await blockchain.GetChainTime(cancellationToken);
        var confirmed = utxos
            .Where(u => u.Confirmed && chain.Height - u.BlockHeight + 1 >= minConfirmations)
            .ToList();

        if (confirmed.Count == 0)
        {
            return new OnchainHtlcStatus(
                OnchainHtlcPhase.AwaitingConfirmations, utxos, Total(utxos));
        }

        var phase = OnchainReceiveGates.RefundIsDue(htlc.RefundLocktime.Value, chain.Timestamp.ToUnixTimeSeconds())
            ? OnchainHtlcPhase.Refundable
            : OnchainHtlcPhase.Claimable;

        return new OnchainHtlcStatus(phase, confirmed, Total(confirmed));
    }

    /// <summary>
    /// Wait until the HTLC holds enough confirmed value to act on, or give up.
    /// </summary>
    /// <param name="blockchain">Where to read from.</param>
    /// <param name="htlc">The HTLC to watch.</param>
    /// <param name="minConfirmations">The count this swap was quoted at.</param>
    /// <param name="within">How long to keep looking.</param>
    /// <param name="poll">How often to look. Defaults to five seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The status once it is claimable, or the last one seen when the time ran out.</returns>
    /// <remarks>
    /// Polling, because an L1 funding raises no event this SDK subscribes to — which is the same
    /// reason the advance pass proposes its onchain actions on every tick rather than on a trigger.
    /// Returning the last status rather than throwing keeps "it never arrived" an answer the caller
    /// can branch on alongside the others.
    /// </remarks>
    public static async Task<OnchainHtlcStatus> AwaitFillAsync(
        IBitcoinBlockchain blockchain,
        OnchainHtlc htlc,
        int minConfirmations,
        TimeSpan within,
        TimeSpan? poll = null,
        CancellationToken cancellationToken = default)
    {
        var interval = poll ?? TimeSpan.FromSeconds(5);
        var deadline = DateTimeOffset.UtcNow + within;
        var status = new OnchainHtlcStatus(OnchainHtlcPhase.Unfunded, [], 0);

        while (true)
        {
            status = await ClassifyAsync(blockchain, htlc, minConfirmations, cancellationToken);
            if (status.Phase is OnchainHtlcPhase.Claimable) return status;
            if (DateTimeOffset.UtcNow + interval > deadline) return status;

            await Task.Delay(interval, cancellationToken);
        }
    }

    /// <summary>
    /// Recover a preimage from the transaction that spent an HTLC.
    /// </summary>
    /// <param name="tx">The spending transaction.</param>
    /// <param name="paymentHash">The hash it must open, as the swap recorded it.</param>
    /// <returns>The preimage, or <c>null</c> when this transaction proved none.</returns>
    /// <remarks>
    /// <para>
    /// The L1 counterpart of <see cref="NArk.ArkadeIntents.Lightning.SwapPreimageReader"/>, which
    /// reads Arkade spends through the indexer and so cannot answer for a Bitcoin transaction.
    /// </para>
    /// <para>
    /// Every candidate is checked against the hash before it is believed. A 32-byte push is not
    /// evidence; one that hashes to this swap's payment hash is, and it is evidence nobody can forge
    /// — which is what makes it safe to act on without trusting whoever built the spend.
    /// </para>
    /// <para>
    /// A <c>null</c> answer is not proof of a refund. A transaction that carried no preimage and one
    /// this failed to parse are the same silence here, and both mean only that the spend is not
    /// provably a claim.
    /// </para>
    /// </remarks>
    public static byte[]? ExtractPreimage(Transaction tx, uint256 paymentHash)
    {
        foreach (var input in tx.Inputs)
        {
            if (input.WitScript is not { } witness) continue;

            foreach (var push in witness.Pushes)
            {
                if (push.Length != OnchainHtlc.PreimageSize) continue;

                // Compared as uint256 rather than as bytes, deliberately. Every caller builds this
                // hash as `new uint256(sha256(P))`, and `uint256` hands its bytes out little-endian
                // by default while a payment hash is written the other way — so a byte comparison
                // here has to pick a convention, and picking the wrong one fails silently: the
                // preimage is simply never found, which reads exactly like a spend that carried
                // none. Rebuilding the same type the caller did leaves no convention to get wrong.
                if (new uint256(NBitcoin.Crypto.Hashes.SHA256(push)) == paymentHash)
                {
                    return push;
                }
            }
        }

        return null;
    }

    private static ulong Total(IEnumerable<BoardingUtxo> utxos) =>
        utxos.Aggregate(0UL, (sum, u) => sum + u.Amount);
}

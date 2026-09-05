using Microsoft.Extensions.Logging;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;
using NArk.Core;
using NArk.Core.Assets;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.ArkadeIntents.Assets;

/// <summary>What became of a deposit, as the transaction that spent it reports.</summary>
public enum OfferSpendKind
{
    /// <summary>
    /// No answer. Not a third outcome — the absence of one, so the caller retries rather than
    /// records.
    /// </summary>
    Indeterminate,

    /// <summary>The deposit went back to the maker, unfilled.</summary>
    Cancelled,

    /// <summary>The solver took it and paid for it.</summary>
    Fulfilled,
}

/// <summary>One swap rebuilt from the chain.</summary>
/// <param name="Intent">The reconstructed row, already saved.</param>
/// <param name="Offer">The offer read out of its funding transaction.</param>
/// <param name="Cancellable">
/// Whether the rebuilt row can still be cancelled. <c>false</c> for everything restored, and see
/// <see cref="OfferRestore"/>'s remarks for why that is a property of the wire format rather than
/// of this scan.
/// </param>
public sealed record RestoredOffer(ArkadeSwapIntent Intent, Offer Offer, bool Cancellable);

/// <summary>What a restore pass found.</summary>
/// <param name="Restored">Swaps rebuilt and written.</param>
/// <param name="Scanned">
/// Txids this pass reached an authoritative answer for — carried so an incremental caller can skip
/// them next time. A txid that could not be fetched is deliberately absent.
/// </param>
/// <param name="Unresolved">
/// Txids that held an offer whose outcome could not be decided yet. Worth rescanning; not worth
/// recording, since recording a guess is how a refunded swap becomes a settled one.
/// </param>
public sealed record OfferRestoreResult(
    IReadOnlyList<RestoredOffer> Restored,
    IReadOnlyList<string> Scanned,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Rebuilding asset-swap rows from the chain after the store that held them is gone.
/// </summary>
/// <remarks>
/// <para>
/// The swap store dies with its storage backend, but nothing in an asset swap is only in that store:
/// the funding transaction carries the offer as an extension packet, the covenant VTXO at the
/// offer's script holds the deposit, and that VTXO's spender says what became of it. So the row is
/// recomputable, and this recomputes it.
/// </para>
/// <para>
/// <b>A restored swap cannot be cancelled.</b> The wire offer carries the maker's x-only key, which
/// is enough to rebuild the address and not enough to sign — the spendable descriptor was only ever
/// local. That is a property of the offer format, not a shortcoming here, and it is reported rather
/// than discovered: <see cref="AssetIntentsManager.CancelSwap"/> refuses a row with no maker
/// descriptor, and <see cref="RestoredOffer.Cancellable"/> says so in advance. A restored swap can
/// still be watched, and still be filled by the solver, which is the outcome it was waiting for.
/// </para>
/// <para>
/// The scan is incremental by design: it takes the candidate txids and returns those it answered,
/// so a caller can persist that set and never fetch the same transaction twice. A txid it could not
/// reach is left out of both lists, which is what keeps a read failure from being remembered as an
/// answer.
/// </para>
/// </remarks>
public static class OfferRestore
{
    /// <summary>
    /// Read the offer out of a funding transaction's extension packet, if it carries one.
    /// </summary>
    /// <param name="tx">The funding transaction.</param>
    /// <returns>The offer, or <c>null</c> when this transaction is not an offer funding.</returns>
    /// <remarks>
    /// Answers <c>null</c> rather than throwing for anything that is simply not an offer — most
    /// transactions in a wallet's history are not, and a scan over them is the normal caller.
    /// A packet that IS an offer and will not decode is a different matter and is left to throw,
    /// because that is a malformed record on the money path rather than an absent one.
    /// </remarks>
    public static Offer? OfferIn(Transaction tx)
    {
        Extension? extension;
        try
        {
            extension = Extension.FromTransaction(tx);
        }
        catch (ArgumentException)
        {
            // A malformed extension is not an offer we can read; it is also not our business to
            // reject the whole transaction over.
            return null;
        }

        var packet = extension?.Packets
            .OfType<UnknownPacket>()
            .FirstOrDefault(p => p.PacketType == OfferCodec.OfferPacketType);

        return packet is null ? null : OfferCodec.Decode(packet.Data);
    }

    /// <summary>
    /// Classify a spend by the covenant leaf it took.
    /// </summary>
    /// <param name="offer">The offer whose covenant was spent.</param>
    /// <param name="server">The Arkade server key the covenant was <em>funded</em> against.</param>
    /// <param name="network">The network the descriptors belong to.</param>
    /// <param name="spend">The transaction that spent the deposit.</param>
    /// <param name="deposit">The deposit outpoint.</param>
    /// <returns>What became of the deposit.</returns>
    /// <remarks>
    /// <para>
    /// The vocabulary is what became of the deposit, not which key moved it. <c>fulfill</c> is the
    /// solver paying for it; <c>cancel</c> and <c>exit</c> both hand it back, differing only in who
    /// had to agree — cooperatively with the signer, or the maker alone after a delay. The outcome
    /// is the same and that is the question being answered.
    /// </para>
    /// <para>
    /// Read from the leaf rather than from what the transaction moved, because the amounts stop
    /// answering once the covenant is a registered contract: the deposit then joins the wallet's own
    /// coins, every wallet-level figure becomes a net delta, and an asset offer's cancel — asset out
    /// of the covenant, same asset back — nets to zero and reads exactly like its fill. Leaves have
    /// no such failure mode, and they survive batching: a solver filling several offers in one
    /// transaction gives each input its own leaf.
    /// </para>
    /// <para>
    /// A server key that has rotated since funding rebuilds a different script, and this answers
    /// <see cref="OfferSpendKind.Indeterminate"/> rather than guessing against the wrong tree.
    /// </para>
    /// </remarks>
    public static OfferSpendKind ClassifySpend(
        Offer offer, OutputDescriptor server, Network network, PSBT spend, OutPoint deposit)
    {
        byte[][] returned;
        byte[]? fulfill;
        try
        {
            var contract = OfferBuilder.BuildContract(offer, server, network);

            // The covenant must be the one that was funded. Comparing here is what stops a rotated
            // server key from producing a confident answer about somebody else's tree.
            if (!contract.GetScriptPubKey().ToBytes().AsSpan().SequenceEqual(offer.SwapPkScript))
            {
                return OfferSpendKind.Indeterminate;
            }

            returned = new[] { "cancel", "exit" }
                .Select(name => contract.FunctionByName(name)?.LeafScript)
                .Where(leaf => leaf is not null)
                .Select(leaf => leaf!)
                .ToArray();
            fulfill = contract.FunctionByName("fulfill")?.LeafScript;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
        {
            // An offer whose covenant will not compile is not classifiable.
            return OfferSpendKind.Indeterminate;
        }

        foreach (var input in spend.Inputs)
        {
            if (input.PrevOut != deposit) continue;
            if (!input.TryGetTaprootLeafScript(out _, out var leafScript)) continue;

            var spent = leafScript.ToBytes();
            if (returned.Any(back => spent.AsSpan().SequenceEqual(back))) return OfferSpendKind.Cancelled;
            if (fulfill is not null && spent.AsSpan().SequenceEqual(fulfill)) return OfferSpendKind.Fulfilled;
        }

        // The deposit left the covenant by none of its leaves — a batch forfeit, say — or this is
        // the wrong half of the spend, or it carries no tapleaf at all.
        return OfferSpendKind.Indeterminate;
    }

    /// <summary>
    /// Rebuild asset-swap rows from candidate funding transactions.
    /// </summary>
    /// <param name="transport">Where virtual transactions are fetched from.</param>
    /// <param name="intentStorage">Where rebuilt rows are written.</param>
    /// <param name="vtxoStorage">The chain view a deposit's fate is read from.</param>
    /// <param name="walletId">The wallet the rebuilt rows belong to.</param>
    /// <param name="candidateTxids">
    /// Transactions worth looking at — a wallet's sent history, minus whatever a previous pass
    /// already answered. Supplied rather than discovered, so any history source serves and this
    /// takes no opinion on how a caller enumerates its own past.
    /// </param>
    /// <param name="serverInfo">The Arkade server's current terms, for the key and the network.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancels between transactions.</param>
    /// <returns>What was rebuilt, what was answered, and what still has no outcome.</returns>
    /// <remarks>
    /// Rows already present are left completely alone, matched by id — an asset swap's id is its
    /// funding txid, so a restore cannot overwrite a live row with a reconstruction that knows less
    /// than it does. In particular it would drop the maker descriptor, and with it the ability to
    /// cancel.
    /// </remarks>
    public static async Task<OfferRestoreResult> RestoreAsync(
        IClientTransport transport,
        IArkadeIntentStorage intentStorage,
        IVtxoStorage vtxoStorage,
        string walletId,
        IReadOnlyCollection<string> candidateTxids,
        ArkServerInfo serverInfo,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var restored = new List<RestoredOffer>();
        var scanned = new List<string>();
        var unresolved = new List<string>();

        var existing = (await intentStorage.GetArkadeSwapIntents(cancellationToken: cancellationToken))
            .Select(i => i.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var txid in candidateTxids.Where(t => !existing.Contains(t)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var funding = await FetchAsync(transport, txid, serverInfo.Network, cancellationToken);
            if (funding is null) continue;   // unreachable, not answered: retried by a later pass

            Offer? offer;
            try
            {
                offer = OfferIn(funding.GetGlobalTransaction());
            }
            catch (FormatException e)
            {
                // The packet is an offer and will not decode. Answered — and answered "not one of
                // ours" — because rescanning it forever cannot make it decode.
                logger?.LogWarning(e, "Transaction {Txid} carries an offer packet that will not decode", txid);
                scanned.Add(txid);
                continue;
            }

            if (offer is null)
            {
                scanned.Add(txid);
                continue;
            }

            var deposit = DepositOf(funding.GetGlobalTransaction(), offer, txid);
            if (deposit is null)
            {
                // An offer that funds nothing at its own covenant is not a swap this can rebuild.
                scanned.Add(txid);
                continue;
            }

            var swapPkScript = Convert.ToHexString(offer.SwapPkScript).ToLowerInvariant();
            var vtxos = await vtxoStorage.GetVtxos(
                scripts: [swapPkScript], includeSpent: true, cancellationToken: cancellationToken);
            var lockup = vtxos.FirstOrDefault(v => v.TransactionId == txid);

            var status = await StatusOfAsync(
                transport, intentStorage, offer, serverInfo, lockup, deposit, cancellationToken);
            if (status is null)
            {
                unresolved.Add(txid);
                continue;
            }

            var isBtcToAsset = offer.WantAsset is not null;
            var intent = new ArkadeSwapIntent
            {
                Id = txid,
                WalletId = walletId,
                Type = isBtcToAsset ? ArkadeSwapIntentType.BtcToAsset : ArkadeSwapIntentType.AssetToBtc,
                OfferAmount = funding.GetGlobalTransaction().Outputs[deposit.N].Value,
                WantAmount = Money.Satoshis(offer.WantAmount),
                Status = status.Value,
                CreatedAt = DateTimeOffset.UtcNow,
                SwapPkScript = swapPkScript,
                SwapAddress = OfferBuilder.BuildContract(offer, serverInfo.SignerKey, serverInfo.Network)
                    .GetArkAddress().ToString(serverInfo.Network == Network.Main),
                FromAssetId = isBtcToAsset ? "btc" : offer.OfferAsset!.ToString(),
                ToAssetId = isBtcToAsset ? offer.WantAsset!.ToString() : "btc",
                SpentTxid = lockup?.ArkTxid ?? lockup?.SpentByTransactionId,
                // No maker descriptor: the wire offer carries only the x-only key, so a restored row
                // can be watched but not cancelled. Recorded honestly rather than left to surface as
                // a failure at the moment somebody tries.
            }.WithAssetMetadata(new AssetSwapMetadata(
                Convert.ToHexString(OfferCodec.Encode(offer)).ToLowerInvariant(), MakerDescriptor: null));

            await intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

            logger?.LogInformation(
                "Restored asset swap {Txid} as {Status} ({Type})", txid, status.Value, intent.Type);

            restored.Add(new RestoredOffer(intent, offer, Cancellable: false));
            scanned.Add(txid);
        }

        return new OfferRestoreResult(restored, scanned, unresolved);
    }

    /// <summary>
    /// The status a rebuilt row should carry, or <c>null</c> when the chain has not decided yet.
    /// </summary>
    private static async Task<ArkadeSwapIntentStatus?> StatusOfAsync(
        IClientTransport transport,
        IArkadeIntentStorage intentStorage,
        Offer offer,
        ArkServerInfo serverInfo,
        ArkVtxo? lockup,
        OutPoint deposit,
        CancellationToken cancellationToken)
    {
        // No sighting of the deposit at all. It may be unsynced rather than absent, so this is not
        // an outcome — the caller rescans.
        if (lockup is null) return null;

        if (lockup.Swept) return ArkadeSwapIntentStatus.Recoverable;
        if (!lockup.IsSpent()) return ArkadeSwapIntentStatus.Pending;

        var spender = lockup.SpentByTransactionId ?? lockup.ArkTxid;
        if (spender is not { Length: > 0 }) return null;

        var spend = await FetchAsync(transport, spender, serverInfo.Network, cancellationToken);
        if (spend is null) return null;

        return ClassifySpend(offer, serverInfo.SignerKey, serverInfo.Network, spend, deposit) switch
        {
            OfferSpendKind.Fulfilled => ArkadeSwapIntentStatus.Fulfilled,
            OfferSpendKind.Cancelled => ArkadeSwapIntentStatus.Cancelled,
            // Spent by a leaf we do not recognise. Recording a guess here is how a returned deposit
            // becomes a settled sale, so nothing is recorded.
            _ => null,
        };
    }

    /// <summary>The output in the funding transaction that pays the offer's own covenant.</summary>
    private static OutPoint? DepositOf(Transaction funding, Offer offer, string txid)
    {
        for (var i = 0; i < funding.Outputs.Count; i++)
        {
            if (funding.Outputs[i].ScriptPubKey.ToBytes().AsSpan().SequenceEqual(offer.SwapPkScript))
            {
                return new OutPoint(uint256.Parse(txid), (uint)i);
            }
        }
        return null;
    }

    private static async Task<PSBT?> FetchAsync(
        IClientTransport transport, string txid, Network network, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> raw;
        try
        {
            raw = await transport.GetVirtualTxsAsync([txid], cancellationToken);
        }
        catch (Exception)
        {
            // Read lag and an outage look alike from here, and neither is an answer about the swap.
            return null;
        }

        foreach (var psbtBase64 in raw)
        {
            if (PSBT.TryParse(psbtBase64, network, out var psbt)) return psbt;
        }
        return null;
    }
}

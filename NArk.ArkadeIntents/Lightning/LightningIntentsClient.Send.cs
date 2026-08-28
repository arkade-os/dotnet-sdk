using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Abstractions;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.SolverRegistry;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core;
using NBitcoin.Scripting;
using NBitcoin;
using System.Security.Cryptography;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>The outcome of funding a Lightning swap: everything needed to watch and account for it.</summary>
/// <param name="RfqId">The negotiation's correlation id.</param>
/// <param name="Quote">The solver's quote, as accepted.</param>
/// <param name="LockupAddress">The swap contract's address, derived locally and verified.</param>
/// <param name="LockupPkScript">The contract's scriptPubKey (hex) — what to watch on-chain.</param>
/// <param name="RefundAddress">Where the covenant refund pays if the swap fails.</param>
/// <param name="PaymentHash">The invoice's payment hash (hex).</param>
/// <param name="FundedSats">What was locked up.</param>
/// <param name="FundingTxid">The Arkade transaction that funded the lockup.</param>
public sealed record FundedLightningSend(
    string RfqId,
    RfqQuote<LightningSendQuoteProfile> Quote,
    string LockupAddress,
    string LockupPkScript,
    string RefundAddress,
    string PaymentHash,
    long FundedSats,
    string FundingTxid);

/// <summary>
/// The maker side of an <c>arkade:BTC-&gt;lightning:BTC</c> swap: pay a BOLT11 out of an Arkade
/// balance by locking sats into a contract only the solver can claim, and only by revealing the
/// preimage that paying the invoice yields.
/// </summary>
/// <remarks>
/// <para>
/// The trust model is the whole design. From a quote this uses <b>only</b> the binding fields
/// (<c>solver_pubkey</c>, <c>refund_locktime</c>, <c>valid_until</c>, the amounts); every other
/// script parameter is the maker's own data — the payment hash from its own invoice, the server key
/// from its own connection, the emulator key from its own fetch, the refund destination from its
/// own wallet. The solver's <c>lockup_address</c> is compared, never used.
/// </para>
/// <para>
/// Once <see cref="SendToLightningAsync"/> returns, the maker may go offline: filling is
/// non-interactive. The solver observes the funding on-chain, pays the invoice and claims with the
/// preimage, which becomes public in the claim witness. A failure refunds by covenant to the
/// maker's own address with no keys, messages or state held here.
/// </para>
/// </remarks>
public sealed partial class LightningIntentsClient
{

    /// <summary>
    /// Negotiate, derive locally, verify, gate and fund — the whole maker flow in one call.
    /// </summary>
    /// <param name="walletId">The wallet paying the invoice and receiving any refund.</param>
    /// <param name="invoice">The BOLT11 to pay.</param>
    /// <param name="rfqTransport">How to reach the solver.</param>
    /// <param name="cancellationToken">Cancels before funding; after funding the swap is live regardless.</param>
    /// <returns>The funded swap.</returns>
    /// <exception cref="RfqRefusedException">The solver declined to quote.</exception>
    /// <exception cref="LockupAddressMismatchException">The solver's address is not ours — nothing was funded.</exception>
    /// <exception cref="LightningSendNotFundableException">A safety gate refused — nothing was funded.</exception>
    public async Task<FundedLightningSend> SendToLightningAsync(
        string walletId,
        string invoice,
        IRfqTransport rfqTransport,
        SolverCard? solverCard = null,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);

        // Decoded from the invoice itself, never taken from the solver: the amount, the payment
        // hash and the expiry are what every gate below is checked against.
        var decoded = BOLT11PaymentRequest.Parse(invoice, serverInfo.Network);
        var invoiceAmountSats = decoded.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
        if (invoiceAmountSats <= 0 || decimal.Truncate(invoiceAmountSats) != invoiceAmountSats)
        {
            throw new ArgumentException(
                $"invoice must carry a whole-satoshi amount, got {invoiceAmountSats}", nameof(invoice));
        }

        // The refund destination is the maker's own fresh receive address. The covenant pins the
        // refund to this script, so it must be ours and it must be settled before we ask for terms.
        var receive = await _contractService.DeriveContract(
            walletId, NextContractPurpose.Receive, cancellationToken: cancellationToken);
        var refundArkAddress = receive.GetArkAddress();
        var refundPkScript = refundArkAddress.ScriptPubKey.ToBytes();
        var refundAddress = refundArkAddress.ToString(serverInfo.Network == Network.Main);

        // The covenant's client-side leaves are keyed by this. Deliberately the same key that owns
        // the refund destination above, so the party who can push the unilateral refund is exactly
        // the party the cooperative refund pays — and so it needs no storage of its own to survive:
        // it is on the wallet's own derivation chain, recoverable like any other address.
        var clientRefund = ClientRefundDescriptorOf(receive);

        var request = LightningSendProfile.Request(
            invoice, refundAddress,
            Convert.ToHexString(clientRefund.ToXOnlyPubKey().ToBytes()).ToLowerInvariant());
        // Held to its own published terms where those exist. A size outside the advertised range is
        // one the solver refuses anyway, and its refusal cannot say by how much — so this is asked
        // before the request rather than after it.
        if (solverCard is not null)
        {
            SolverTerms.AssertWithinLimits(solverCard, LightningSendProfile.Pair, (long)invoiceAmountSats);
        }

        var quote = await rfqTransport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(
            request, cancellationToken);

        // The card is the only statement of terms with provenance — signed, reviewed, tied to a
        // discoverable identity — while a quote is whatever arrived on a socket. Comparing one
        // against the other is the only way to catch a solver quoting differently from how it
        // advertises, which no amount of checking a quote against itself can reveal.
        if (solverCard is not null)
        {
            SolverTerms.AssertFeeWithinAdvertised(solverCard, quote);
        }

        var (eightLeaf, nineLeaf) = await DeriveLockupAsync(
            quote, decoded, refundPkScript, clientRefund, serverInfo, cancellationToken);
        var isMainnet = serverInfo.Network == Network.Main;

        // Accepts whichever of the two shapes matches — see VHTLCv2Contract's own remarks on
        // NonInteractiveRefundWithoutReceiver. Nothing on the wire says which one this solver has
        // deployed, and refusing to fund is still the outcome when the quote matches neither.
        var contract = LightningSendGates.ResolveLockupContract(quote, eightLeaf, nineLeaf, isMainnet);
        var lockupArkAddress = contract.GetArkAddress();
        var lockupAddress = lockupArkAddress.ToString(isMainnet);

        // Checked here, immediately before the irreversible step — not when the quote arrived.
        LightningSendGates.AssertFundable(
            quote,
            (long)invoiceAmountSats,
            decoded.ExpiryDate.ToUnixTimeSeconds(),
            _time.GetUtcNow().ToUnixTimeSeconds());

        // Imported BEFORE funding. This is the only place the funded script is persisted in a
        // rebuildable form — the contract's serialized program and args carry the solver key, both
        // timelocks, the emulator key and the refund destination, which is what the refund path
        // reconstructs itself from. (Watching does not depend on it: the intent row saved below is
        // itself an IActiveScriptsProvider.) Importing after the spend would leave a window where
        // the money is out and the only record of how to refund it does not exist yet.
        await _contractService.ImportContract(
            walletId,
            contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"lightning-swap:{request.RfqId}" },
            cancellationToken: cancellationToken);

        // Recorded BEFORE the spend, and deliberately so. The contract import above makes the
        // funded script rebuildable; this makes the swap FINDABLE. Without it a crash between the
        // spend and the record leaves money in a covenant that nothing is watching and no sweep
        // knows to look for — the one failure here with no way back. The reverse mistake, a row for
        // a spend that never landed, costs nothing and is exactly what reconciliation can spot.
        var intent = new ArkadeSwapIntent
        {
            Id = request.RfqId,
            WalletId = walletId,
            Type = ArkadeSwapIntentType.BtcToLightning,
            OfferAmount = Money.Satoshis(quote.FromAmount),
            WantAmount = Money.Satoshis(quote.ToAmount),
            Status = ArkadeSwapIntentStatus.Funding,
            CreatedAt = _time.GetUtcNow(),
            SwapPkScript = lockupArkAddress.ScriptPubKey.ToHex(),
            SwapAddress = lockupAddress,
            // No offer TLV: this direction is negotiated by RFQ, and the covenant is rebuilt from
            // the imported contract rather than from a wire offer.
            OfferHex = "",
            FromAssetId = "btc",
            ToAssetId = "lightning:btc",
            Invoice = invoice,
            PaymentHash = decoded.PaymentHash.ToString(),
            RefundLocktime = quote.RefundLocktime,
        };
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        var txid = await _spendingService.Spend(
            walletId,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(quote.FromAmount), lockupArkAddress)],
            cancellationToken);

        intent.Status = ArkadeSwapIntentStatus.Pending;
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        return new FundedLightningSend(
            RfqId: request.RfqId,
            Quote: quote,
            LockupAddress: lockupAddress,
            LockupPkScript: lockupArkAddress.ScriptPubKey.ToHex(),
            RefundAddress: refundAddress,
            PaymentHash: decoded.PaymentHash.ToString(),
            FundedSats: quote.FromAmount,
            FundingTxid: txid.ToString());
    }

    /// <summary>
    /// Reclaim the deposit of a swap the solver never filled.
    /// </summary>
    /// <param name="swapId">The swap's id (its RFQ correlation id).</param>
    /// <param name="cancellationToken">Cancels before the spend.</param>
    /// <returns>The swap, moved to <see cref="ArkadeSwapIntentStatus.Cancelled"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The swap is unknown, is not a Lightning swap, is not awaiting a refund, or its deadline has
    /// not passed.
    /// </exception>
    /// <remarks>
    /// The swap moves to <see cref="ArkadeSwapIntentStatus.Cancelling"/> before the spend so the
    /// monitor cannot read our own refund as something the solver did, and rolls back on failure.
    /// </remarks>
    /// <summary>
    /// How far median time past can trail the wall clock before a CLTV leaf is spendable.
    /// </summary>
    /// <remarks>
    /// MTP is the median of the last eleven block times, so it lags real time by roughly half that
    /// span and by more when blocks come slowly. A refund built the moment our own clock passes the
    /// locktime is one the chain refuses, which surfaces as a failed spend rather than as "too
    /// early". The reference implementation gives itself the same window, retrying across it.
    /// </remarks>
    internal const long MedianTimePastLagSeconds = 2 * 60 * 60;

    /// <summary>
    /// The outputs a refund may spend: every live output sitting at the lockup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of them, not the first. A lockup can hold more than one output — a retried funding, or a
    /// counterparty that split it — and refunding one would leave the rest behind with no second
    /// path to them: the leaf that made this possible is ours alone, and nothing else will come
    /// looking. There is no amount gate here, unlike a claim: a refund publishes no secret and pays
    /// an address the covenant already committed to, so taking more than was quoted costs nobody
    /// anything and taking less strands it.
    /// </para>
    /// <para>
    /// Swept outputs are named rather than skipped. Silently filtering them turns "the operator
    /// swept your deposit, recover it elsewhere" into "there is nothing here", which is the same
    /// sentence a wallet uses for an empty address.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Nothing live is left at the lockup.</exception>
    internal static IReadOnlyList<ArkVtxo> SelectRefundable(
        IReadOnlyCollection<ArkVtxo> vtxos, string swapId)
    {
        var live = vtxos.Where(v => !v.IsSpent() && !v.Swept).ToList();
        if (live.Count > 0) return live;

        var swept = vtxos.Where(v => v.Swept && !v.IsSpent()).ToList();
        throw new InvalidOperationException(
            swept.Count == 0
                ? $"Swap '{swapId}' has no unspent output at its lockup address — there is nothing to refund."
                : $"Swap '{swapId}' has {swept.Count} output(s) at its lockup address, but the operator "
                  + "has swept them, so this leaf can no longer spend them: "
                  + string.Join(", ", swept.Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}")));
    }

    /// <summary>
    /// Refuses until the chain will actually accept a spend of the CLTV leaf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The leaf matures against MEDIAN TIME PAST — the median of the last eleven block times —
    /// which trails real time and trails it further when blocks come slowly. A refund built the
    /// instant our own clock passes the locktime is one the chain refuses, and that refusal arrives
    /// as a failed spend rather than as "too early".
    /// </para>
    /// <para>
    /// So the question is asked of the chain rather than answered from a clock, the same way
    /// <c>VHTLCContractTransformer</c> asks it for the v1 contract. That makes the refund possible
    /// exactly when it is possible, instead of after a fixed pessimistic wait.
    /// </para>
    /// <para>
    /// Without a blockchain to ask, the wall clock plus the worst-case lag is the honest fallback:
    /// it can only be too patient, never too eager, and being too eager here means broadcasting a
    /// spend that cannot confirm.
    /// </para>
    /// </remarks>
    private async Task AssertLocktimeReachedAsync(
        string swapId, long locktime, CancellationToken cancellationToken)
    {
        var chainNow = _blockchain is null
            ? (long?)null
            : (await _blockchain.GetChainTime(cancellationToken)).Timestamp.ToUnixTimeSeconds();

        AssertLocktimeReached(swapId, locktime, chainNow, _time.GetUtcNow().ToUnixTimeSeconds());
    }

    /// <summary>
    /// Whether a refund may be built, given what the chain says and what our clock says.
    /// </summary>
    /// <param name="swapId">The swap, for the message.</param>
    /// <param name="locktime">The covenant's refund locktime, unix seconds.</param>
    /// <param name="chainNow">The chain's median time past, or <c>null</c> when unavailable.</param>
    /// <param name="wallClockNow">Our own clock, unix seconds.</param>
    /// <exception cref="InvalidOperationException">The leaf has not matured.</exception>
    /// <remarks>
    /// <para>
    /// Separated from the fetch so the rule can be exercised without a client and its eight
    /// dependencies — and so the decision is a function of its inputs rather than of whatever the
    /// world happened to answer.
    /// </para>
    /// <para>
    /// Median time past is the median of the last eleven block times. It trails real time, and
    /// trails it further when blocks come slowly, so the two disagree exactly when it matters: a
    /// spend built on the clock's word goes into a chain that will not confirm it, and the failure
    /// reads like a broken covenant rather than an early attempt.
    /// </para>
    /// <para>
    /// With no chain to ask, the clock plus the worst-case lag is the honest fallback. It can only
    /// be too patient, and being too eager here means broadcasting a spend that cannot confirm.
    /// </para>
    /// </remarks>
    internal static void AssertLocktimeReached(
        string swapId, long locktime, long? chainNow, long wallClockNow)
    {
        if (chainNow is { } mtp)
        {
            if (mtp < locktime)
            {
                throw new InvalidOperationException(
                    $"Swap '{swapId}' cannot be refunded yet: the chain's median time past is " +
                    $"{locktime - mtp}s short of its refund locktime.");
            }
            return;
        }

        var takeableAt = locktime + MedianTimePastLagSeconds;
        if (wallClockNow < takeableAt)
        {
            throw new InvalidOperationException(
                $"Swap '{swapId}' cannot be refunded for another {takeableAt - wallClockNow}s — its " +
                $"locktime passes in {Math.Max(0, locktime - wallClockNow)}s, and with no chain time " +
                $"available this waits out the {MedianTimePastLagSeconds}s median-time-past lag on top.");
        }
    }

    public async Task<ArkadeSwapIntent> RefundSwap(string swapId, CancellationToken cancellationToken = default)
    {
        var intent = await _intentStorage.GetArkadeSwapIntent(swapId, cancellationToken)
                     ?? throw new InvalidOperationException($"Swap '{swapId}' not found.");

        if (intent.Type != ArkadeSwapIntentType.BtcToLightning)
            throw new InvalidOperationException($"Swap '{swapId}' is not a Lightning swap ({intent.Type}).");
        if (intent.Status is not (ArkadeSwapIntentStatus.Refundable or ArkadeSwapIntentStatus.Pending))
            throw new InvalidOperationException($"Swap '{swapId}' is not awaiting a refund (status {intent.Status}).");
        if (intent.RefundLocktime is not { } locktime)
            throw new InvalidOperationException($"Swap '{swapId}' has no refund locktime recorded.");

        await AssertLocktimeReachedAsync(swapId, locktime, cancellationToken);

        var previousStatus = intent.Status;
        intent.Status = ArkadeSwapIntentStatus.Cancelling;
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        try
        {
            var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
            var contract = await LightningCorridor.LoadLockupAsync(
                _contractStorage, intent.SwapPkScript, intent.Id, serverInfo.Network, cancellationToken);

            var vtxos = await _vtxoStorage.GetVtxos(
                scripts: [intent.SwapPkScript], cancellationToken: cancellationToken);
            var refundable = SelectRefundable(vtxos, swapId);

            // `refundWithoutReceiver`: our own key plus the server, once the locktime checked above
            // has passed. Deliberately not the faster `nonInteractiveRefund` leaf — that one needs
            // the solver's signature, so it is a path the two of us take by agreement, not one we
            // can take alone. This is the exit that depends on no counterparty.
            var coins = refundable
                .Select(v => contract.ToRefundWithoutReceiverCoin(intent.WalletId, v))
                .ToArray();
            var total = refundable.Aggregate(0UL, (sum, v) => sum + v.Amount);

            // This leaf carries no covenant, so nothing on-chain pins the payout — we choose it. It
            // still goes to the address committed at funding time, read back off the contract rather
            // than derived afresh, so a refund can never land somewhere the swap never named.
            var destination = RefundAddressOf(contract, serverInfo.SignerKey.ToXOnlyPubKey());
            var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis((long)total), destination);

            var txid = await _spendingService.Spend(intent.WalletId, coins, [output], cancellationToken);

            intent.Status = ArkadeSwapIntentStatus.Cancelled;
            intent.SpentTxid = txid.ToString();
            await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

            _logger?.LogInformation("Refunded Lightning swap {SwapId} in {Txid}", swapId, txid);
            return intent;
        }
        catch
        {
            // Roll back so a later attempt — ours or anyone else's — is still possible.
            intent.Status = previousStatus;
            await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// The maker's own payout address, as committed when the swap was funded.
    /// </summary>
    /// <param name="contract">The rebuilt lockup contract.</param>
    /// <param name="serverKey">The Arkade server key the address is derived against.</param>
    /// <returns>Where a refund of this swap pays.</returns>
    /// <remarks>
    /// Taken from the script the <c>nonInteractiveRefund</c> covenant pins its payout to. That leaf
    /// is not the one we spend through, but it is where the destination was committed at funding
    /// time, so reading it back is what keeps every refund path — ours and the solver's — paying the
    /// same place.
    /// </remarks>
    public static ArkAddress RefundAddressOf(VHTLCv2Contract contract, NBitcoin.Secp256k1.ECXOnlyPubKey serverKey) =>
        ArkAddress.FromScriptPubKey(new Script(contract.NonInteractiveRefundPkScript), serverKey);

    // Builds both lockup shapes from the quote's binding fields and the maker's own data. The one
    // value taken from the non-binding half is `receiver_pk_script`, and only because every leaf
    // feeds the merkle root: without the solver's own claim destination the address cannot be
    // reproduced at all. Safe to take, because that leaf pays the solver — a wrong value costs it a
    // spending path and the maker nothing.
    private async Task<(VHTLCv2Contract EightLeaf, VHTLCv2Contract NineLeaf)> DeriveLockupAsync(
        RfqQuote<LightningSendQuoteProfile> quote,
        BOLT11PaymentRequest invoice,
        byte[] refundPkScript,
        OutputDescriptor clientRefund,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        var delays = LightningCorridor.UnilateralDelays(serverInfo);

        var receiverPkScript = quote.Profile?.ReceiverPkScript
            ?? throw new InvalidOperationException(
                "the quote carries no receiver_pk_script, so the covenant's nonInteractiveClaim leaf " +
                "cannot be reconstructed and the lockup address cannot be derived");

        return LightningCorridor.DeriveBothLockupShapes(
            serverInfo.SignerKey,
            // Roles are positional: on this corridor the maker sends and the solver receives.
            sender: clientRefund,
            receiver: LightningCorridor.DescriptorForXOnly(quote.SolverPubkey, serverInfo.Network),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(invoice.PaymentHash), false),
            new LockTime(checked((uint)quote.RefundLocktime)),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            LightningCorridor.NormalizeToXOnly(
                Convert.FromHexString(EmulatorPubKeys.Resolve(serverInfo.NetworkName, _emulatorPubkeyOverride))),
            nonInteractiveClaimPkScript: Convert.FromHexString(receiverPkScript),
            nonInteractiveRefundPkScript: refundPkScript);
    }

    // The key the covenant's client-side refund leaves are built around, and the one that signs
    // them: the same descriptor that owns the refund destination, so the party who can push the
    // refund is exactly the party it pays.
    private static OutputDescriptor ClientRefundDescriptorOf(ArkContract receive) =>
        UserKeyOf(receive, "client refund");

}

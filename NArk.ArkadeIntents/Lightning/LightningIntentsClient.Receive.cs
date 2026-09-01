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

/// <summary>A negotiated receive swap, waiting for someone to pay its invoice.</summary>
/// <param name="RfqId">The negotiation's correlation id.</param>
/// <param name="Quote">The solver's quote, as accepted.</param>
/// <param name="Invoice">The hold invoice to hand to the payer.</param>
/// <param name="Preimage">
/// The secret that settles both sides. Hold on to it: the swap cannot complete without it, and
/// nobody else has it in the clear.
/// </param>
/// <param name="PaymentHash"><c>sha256(Preimage)</c>, hex.</param>
/// <param name="Contract">The funding contract, derived locally.</param>
/// <param name="LockupAddress">That contract's address — where the solver must pay.</param>
/// <param name="PayoutAddress">The client's own address the claim pays out to.</param>
public sealed record PendingLightningReceive(
    string RfqId,
    RfqQuote<LightningReceiveQuoteProfile> Quote,
    string Invoice,
    byte[] Preimage,
    string PaymentHash,
    VHTLCv2Contract Contract,
    string LockupAddress,
    string PayoutAddress);

/// <summary>
/// The client side of a <c>lightning:BTC-&gt;arkade:BTC</c> swap: be paid over Lightning and take
/// delivery on Arkade.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="LightningIntentsClient"/>, and the exposure mirrors with it. Here the
/// <em>solver</em> pays out first: it funds the Arkade contract while the Lightning payment it is
/// owed is still held, and only gets paid when the client's claim publishes the preimage. That is
/// why the client chooses the secret — a solver that could settle the invoice on its own would be
/// paid for a swap it never delivered.
/// </para>
/// <para>
/// The client's own protection is the same as on the send leg: derive the contract locally from its
/// own data plus the quote's binding fields, and refuse on any mismatch. Nothing here trusts the
/// solver's <c>lockup_address</c> or its invoice beyond checking both against what was asked for.
/// </para>
/// <para>
/// Verified against a live solver on regtest: the quote it returns carries the invoice, the lockup
/// address and the solver's refund script, and the address reproduces locally leaf for leaf. What
/// has not run end to end is the part after that — funding observed, claim broadcast, invoice
/// settled.
/// </para>
/// </remarks>
public sealed partial class LightningIntentsClient
{

    /// <summary>
    /// Negotiate a receive swap and verify everything the solver sent back.
    /// </summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="amountSats">The size to ask for, in sats — of the leg <paramref name="amountSide"/> names.</param>
    /// <param name="rfqTransport">How to reach the solver.</param>
    /// <param name="amountSide">
    /// Which leg <paramref name="amountSats"/> pins, and so who absorbs the solver's spread.
    /// <see cref="RfqAmountSide.To"/> fixes what lands on Arkade and bills the payer more;
    /// <see cref="RfqAmountSide.From"/> fixes the payer's bill and nets the spread out of the payout.
    /// Defaults to <see cref="RfqAmountSide.To"/>, which is what a caller asking to "receive N sats"
    /// means. A merchant minting an invoice for an order total wants <see cref="RfqAmountSide.From"/>:
    /// a LUD-06 wallet checks the invoice against the amount its user approved and refuses anything
    /// larger.
    /// </param>
    /// <param name="solverCard">
    /// The solver's published card, when there is one. Supplying it holds the solver to its own
    /// advertised limits and fee. Omitting it is not a check skipped but one that does not apply — a
    /// deployment naming a solver outright has no published terms to hold it to.
    /// </param>
    /// <param name="covclaimdPubKey">
    /// covclaimd's compressed key, read live from its own endpoint. The preimage is sealed to this,
    /// so the claim can be pushed without the client online.
    /// </param>
    /// <param name="cancellationToken">Cancels the negotiation.</param>
    /// <returns>The invoice to be paid, and everything needed to claim once it is.</returns>
    /// <exception cref="RfqRefusedException">The solver declined to quote.</exception>
    /// <exception cref="LightningReceiveNotUsableException">The quote did not survive the client's own checks.</exception>
    /// <exception cref="LockupAddressMismatchException">The solver's address is not ours.</exception>
    public async Task<PendingLightningReceive> ReceiveFromLightningAsync(
        string walletId,
        long amountSats,
        IRfqTransport rfqTransport,
        string covclaimdPubKey,
        SolverCard? solverCard = null,
        RfqAmountSide amountSide = RfqAmountSide.To,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);

        // The payout contract is the client's own fresh receive address, and its key is what claims
        // the swap — on this corridor the client is the covenant's `receiver`.
        var payout = await _contractService.DeriveContract(
            walletId, NextContractPurpose.Receive, cancellationToken: cancellationToken);
        var payoutArkAddress = payout.GetArkAddress();
        var payoutPkScript = payoutArkAddress.ScriptPubKey.ToBytes();
        var payoutAddress = payoutArkAddress.ToString(serverInfo.Network == Network.Main);
        var payoutDescriptor = UserKeyOf(payout, "payout");

        // The negotiation id first: for a wallet whose claim key repeats across swaps it is also
        // the preimage salt, so it has to exist before the preimage does.
        var rfqId = RfqProtocol.NewRfqId();
        var preimage = await ProvisionClaimPreimageAsync(walletId, payoutDescriptor, rfqId, cancellationToken);
        var sealed_ = await ClaimPacket.SealAsync(preimage, covclaimdPubKey, _cipher, cancellationToken);

        var request = LightningReceiveProfile.Request(
            amountSats,
            amountSide,
            sealed_.PaymentHash,
            payoutAddress,
            Convert.ToHexString(payoutDescriptor.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
            sealed_.Packet,
            rfqId);

        // Asked before the request: a size outside the advertised range is one the solver refuses
        // anyway, and its refusal cannot say by how much. A card states its bounds on the to leg, so
        // against an exact-in size this is indicative rather than exact — it reads the request's
        // figure as a payout when the payout will in fact be a fee lower. Left approximate on
        // purpose: the exact answer needs the quote, and the quote is what this pre-check exists to
        // avoid spending on a size that was never servable.
        if (solverCard is not null)
        {
            SolverTerms.AssertWithinLimits(solverCard, LightningReceiveProfile.Pair, amountSats);
        }

        var quote = await rfqTransport
            .RequestQuoteAsync<LightningReceiveRequestProfile, LightningReceiveQuoteProfile>(
                request, cancellationToken);

        // Held to its own published terms. A quote is whatever arrived on a socket; the card is
        // signed, reviewed and tied to a discoverable identity, and only comparing the two catches a
        // solver quoting differently from how it advertises.
        if (solverCard is not null)
        {
            SolverTerms.AssertFeeWithinAdvertised(solverCard, quote);
        }

        var invoice = LightningReceiveGates.VerifyInvoice(
            quote, sealed_.PaymentHash, amountSats, amountSide, serverInfo.Network);

        // The last check before the invoice can reach a payer: paying into a window too short to
        // claim in parks the payer's money in a held HTLC until it lapses, and a quote billing more
        // than the deployment allows is one whose invoice should never be handed out.
        LightningReceiveGates.AssertReceivable(
            quote, invoice, _time.GetUtcNow().ToUnixTimeSeconds(), _maxPayAmountSats);

        var contract = await DeriveLockupAsync(
            quote, sealed_.PaymentHash, payoutDescriptor, payoutPkScript, serverInfo, cancellationToken);
        var lockupAddress = contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);

        if (quote.Profile?.LockupAddress is { } quoted && quoted != lockupAddress)
        {
            throw new LockupAddressMismatchException(lockupAddress, quoted);
        }

        // Imported and recorded BEFORE the invoice is handed out. Once a payer has it the solver
        // can fund at any moment, and from that point the swap is only claimable by whoever holds
        // the preimage — so the row carrying it has to exist first. There is no recovering it
        // afterwards: we chose it, and the only other copy is sealed to a key we do not hold.
        await _contractService.ImportContract(
            walletId,
            contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"lightning-receive:{request.RfqId}" },
            cancellationToken: cancellationToken);

        await _intentStorage.SaveArkadeSwapIntent(new ArkadeSwapIntent
        {
            Id = request.RfqId,
            WalletId = walletId,
            Type = ArkadeSwapIntentType.LightningToBtc,
            OfferAmount = Money.Satoshis(quote.FromAmount),
            WantAmount = Money.Satoshis(quote.ToAmount),
            Status = ArkadeSwapIntentStatus.Pending,
            CreatedAt = _time.GetUtcNow(),
            SwapPkScript = contract.GetScriptPubKey().ToHex(),
            SwapAddress = lockupAddress,
            // No offer TLV: negotiated by RFQ, and the covenant is rebuilt from the imported
            // contract rather than from a wire offer.
            OfferHex = "",
            FromAssetId = "lightning:btc",
            ToAssetId = "btc",
            Invoice = invoice.ToString(),
            PaymentHash = sealed_.PaymentHash,
            Preimage = Convert.ToHexString(sealed_.Preimage).ToLowerInvariant(),
            RefundLocktime = quote.RefundLocktime,
        }, cancellationToken);

        _logger?.LogInformation(
            "Receive swap {RfqId} negotiated: {Amount} sats to {Payout}, lockup {Lockup}",
            request.RfqId, amountSats, payoutAddress, lockupAddress);

        return new PendingLightningReceive(
            request.RfqId, quote, invoice.ToString(), sealed_.Preimage, sealed_.PaymentHash,
            contract, lockupAddress, payoutAddress);
    }

    /// <summary>
    /// Take delivery: spend the lockup the solver funded, revealing the preimage.
    /// </summary>
    /// <param name="swapId">The negotiation's correlation id.</param>
    /// <param name="cancellationToken">Cancels before the spend; after it the claim is live regardless.</param>
    /// <returns>The updated intent.</returns>
    /// <exception cref="InvalidOperationException">
    /// No such swap, the wrong direction, nothing funded yet, or the solver's reclaim window has
    /// already opened.
    /// </exception>
    /// <remarks>
    /// This both takes delivery and pays the solver: the preimage becomes public in the witness, and
    /// that is what lets the held invoice settle. So it is not an optional tidy-up — a swap left
    /// unclaimed past <c>refund_locktime</c> is one where the solver reclaims its lockup and the
    /// payer's money was never earned.
    /// </remarks>
    public async Task<ArkadeSwapIntent> ClaimAsync(
        string swapId, CancellationToken cancellationToken = default)
    {
        var intent = await _intentStorage.GetArkadeSwapIntent(swapId, cancellationToken)
                     ?? throw new InvalidOperationException($"Swap '{swapId}' not found.");

        if (intent.Type != ArkadeSwapIntentType.LightningToBtc)
            throw new InvalidOperationException($"Swap '{swapId}' is not a Lightning receive ({intent.Type}).");
        if (intent.RefundLocktime is not { } locktime)
            throw new InvalidOperationException($"Swap '{swapId}' has no refund locktime recorded.");

        // Past this the solver's own reclaim path is open, so a claim would be racing it for the
        // same output. Better to refuse than to broadcast a spend that may already be stale.
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (now >= locktime)
        {
            throw new InvalidOperationException(
                $"Swap '{swapId}' passed its claim window {now - locktime}s ago; the solver's reclaim path is open.");
        }

        var serverInfo = await _transport.GetServerInfoAsync(cancellationToken);
        var contract = await LightningCorridor.LoadLockupAsync(
            _contractStorage, intent.SwapPkScript, intent.Id, serverInfo.Network, cancellationToken);

        var preimage = intent.Preimage is { Length: > 0 } preimageHex
            ? Convert.FromHexString(preimageHex)
            : await RederivePreimageAsync(intent, contract, cancellationToken);

        var vtxos = await _vtxoStorage.GetVtxos(
            scripts: [intent.SwapPkScript], cancellationToken: cancellationToken);
        var claimable = SelectClaimable(vtxos, (ulong)intent.WantAmount.Satoshi, swapId);
        var coins = claimable.Select(v => contract.ToClaimCoin(intent.WalletId, v, preimage)).ToArray();
        var total = claimable.Aggregate(0UL, (sum, v) => sum + v.Amount);

        // Where the claim pays was fixed at negotiation time, in the leaf that pins our payout.
        // Reading it back rather than deriving afresh keeps a claim from ever landing somewhere the
        // swap did not name.
        var destination = ArkAddress.FromScriptPubKey(
            new Script(
                contract.EmulatorCovenants?.ReceiverPkScript
                ?? throw new InvalidOperationException(
                    $"Swap '{swapId}'s lockup carries no emulator covenant suite, so it commits to " +
                    "no claim destination — refusing to claim to an address the swap did not name.")),
            serverInfo.SignerKey.ToXOnlyPubKey());
        var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis((long)total), destination);

        var txid = await _spendingService.Spend(intent.WalletId, coins, [output], cancellationToken);

        intent.Status = ArkadeSwapIntentStatus.Fulfilled;
        intent.SpentTxid = txid.ToString();
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        _logger?.LogInformation("Claimed Lightning receive swap {SwapId} in {Txid}", swapId, txid);
        return intent;
    }

    /// <summary>
    /// The outputs a claim may spend: every live output at the lockup, provided they cover what the
    /// swap promised.
    /// </summary>
    /// <remarks>
    /// The gate is on the SUM, never on the address alone. The lockup address is public from the
    /// moment we hold a quote, so anyone can put an output there — and claiming is not a neutral
    /// act: it publishes the preimage. Claiming for less than the quoted amount would hand over the
    /// secret that settles the payer's invoice in exchange for whatever happened to be there.
    ///
    /// Everything live is claimed together, the way the reference client does it. A retried or
    /// split funding leaves several outputs at one address, and claiming only some of them can
    /// leave the solver's watched outpoint unspent — the swap settled, yet the counterparty never
    /// sees the preimage it is owed.
    /// </remarks>
    /// <summary>
    /// The secret this swap will be claimed with, derived from the wallet wherever possible.
    /// </summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="payoutDescriptor">The claim key — what the derivation is anchored to.</param>
    /// <param name="rfqId">The negotiation id, which doubles as the salt. See the remarks.</param>
    /// <param name="cancellationToken">Cancels the signature.</param>
    /// <returns>32 bytes.</returns>
    /// <remarks>
    /// <para>
    /// Random bytes cannot be recovered. A preimage that exists only in a database is a swap whose
    /// funds die with that database — the lockup stays payable to whoever holds the secret, and
    /// nobody does. Deriving it from the wallet's own signature makes the seed enough.
    /// </para>
    /// <para>
    /// Two arms, because one does not cover both wallet shapes. An HD wallet gets a fresh child
    /// descriptor per swap, so the message can pin its index and still be unique. A single-key
    /// wallet has one key: pinning an index there would hand <b>every</b> swap the same preimage,
    /// and one counterparty learning its own would learn all of them. Uniqueness has to come from
    /// the message instead, which is what the salt is for.
    /// </para>
    /// <para>
    /// The salt is the negotiation id rather than fresh randomness. It is already unique per swap,
    /// already public, and already the record's own key — so nothing extra has to be stored or
    /// migrated for a swap to be re-derivable. It never reaches the wire; only the payment hash
    /// does, so this choice is ours alone and costs no compatibility.
    /// </para>
    /// <para>
    /// A wallet that cannot sign falls back to randomness. That swap is claimable and not
    /// recoverable, which is the honest outcome for a wallet holding no key.
    /// </para>
    /// </remarks>
    private async Task<byte[]> ProvisionClaimPreimageAsync(
        string walletId, OutputDescriptor payoutDescriptor, string rfqId,
        CancellationToken cancellationToken)
    {
        var signer = await _walletProvider.GetSignerAsync(walletId, cancellationToken);
        if (signer is null)
        {
            _logger?.LogWarning(
                "Wallet {WalletId} cannot sign, so this swap's preimage is random and will not "
                + "survive the loss of its record", walletId);
            return RandomNumberGenerator.GetBytes(32);
        }

        var salt = PreimageProvisioning.IsPerArtifactDescriptor(payoutDescriptor)
            ? null
            : Convert.FromHexString(rfqId);

        return await PreimageProvisioning.DerivePreimageAsync(
            signer, payoutDescriptor, salt, cancellationToken);
    }

    /// <summary>
    /// Rebuilds a swap's preimage from the wallet, for a record that no longer carries it.
    /// </summary>
    /// <remarks>
    /// Both inputs survive independently of the secret: the claim key is the covenant's own
    /// <c>receiver</c>, read back off the contract, and the salt is the swap's id. So a record
    /// stripped of its preimage — or one restored from a backup that never held one — can still
    /// produce the secret, provided the wallet that made it is present.
    /// </remarks>
    private async Task<byte[]> RederivePreimageAsync(
        ArkadeSwapIntent intent, VHTLCv2Contract contract, CancellationToken cancellationToken)
    {
        var signer = await _walletProvider.GetSignerAsync(intent.WalletId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Swap '{intent.Id}' has no stored preimage and its wallet cannot sign, so the "
                + "secret that claims it cannot be rebuilt.");

        var salt = PreimageProvisioning.IsPerArtifactDescriptor(contract.Receiver)
            ? null
            : Convert.FromHexString(intent.Id);

        var preimage = await PreimageProvisioning.DerivePreimageAsync(
            signer, contract.Receiver, salt, cancellationToken);

        // Proven, not assumed: the covenant commits to hash160(sha256(P)), so a wrong derivation
        // produces a witness the script rejects — and finding that out at broadcast, after the
        // claim window has been spent, is the one place this must not be discovered.
        var rebuilt = new uint160(SwapScriptValues.PreimageHashFromPaymentHash(
            System.Security.Cryptography.SHA256.HashData(preimage)), false);
        if (rebuilt != contract.Hash)
        {
            throw new InvalidOperationException(
                $"Swap '{intent.Id}' has no stored preimage and the one derived from this wallet "
                + "does not match the covenant's hash — it is not this swap's secret.");
        }

        return preimage;
    }

    internal static IReadOnlyList<ArkVtxo> SelectClaimable(
        IReadOnlyCollection<ArkVtxo> vtxos, ulong expectedSats, string swapId)
    {
        var live = vtxos.Where(v => !v.IsSpent() && !v.Swept).ToList();
        if (live.Count == 0)
        {
            throw new InvalidOperationException(
                $"Swap '{swapId}' has no unspent lockup — the solver has not funded it yet.");
        }

        var total = live.Aggregate(0UL, (sum, v) => sum + v.Amount);
        if (total < expectedSats)
        {
            throw new InvalidOperationException(
                $"Swap '{swapId}' holds {total} sats across {live.Count} output(s), less than the " +
                $"quoted {expectedSats} — refusing to publish the preimage for less than the swap promised.");
        }

        return live;
    }

    // Build the funding contract from the quote's binding fields and the client's own data. Roles
    // invert here relative to the send leg: the solver funds, so it is the covenant's
    // destinations follow them — <c>nonInteractiveClaim</c> pays the client,
    // <c>nonInteractiveRefund</c> pays the solver.
    private async Task<VHTLCv2Contract> DeriveLockupAsync(
        RfqQuote<LightningReceiveQuoteProfile> quote,
        string paymentHash,
        OutputDescriptor payoutDescriptor,
        byte[] payoutPkScript,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        var delays = LightningCorridor.UnilateralDelays(serverInfo);

        var solverRefundPkScript = quote.Profile?.SolverRefundPkScript
            ?? throw new InvalidOperationException(
                "the quote carries no solver_refund_pk_script, so the covenant's nonInteractiveRefund " +
                "leaf cannot be reconstructed and the lockup address cannot be derived");

        return new VHTLCv2Contract(
            serverInfo.SignerKey,
            sender: LightningCorridor.DescriptorForXOnly(quote.SolverPubkey, serverInfo.Network),
            receiver: payoutDescriptor,
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(paymentHash)), false),
            new LockTime(checked((uint)quote.RefundLocktime)),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            // The deployed reference solver funds the pre-timelocked-refund shape; derive that,
            // since nothing on the wire says otherwise and the quoted address is the agreement.
            new EmulatorCovenants(
                LightningCorridor.NormalizeToXOnly(
                    Convert.FromHexString(EmulatorPubKeys.Resolve(serverInfo.NetworkName, _emulatorPubkeyOverride))),
                receiverPkScript: payoutPkScript,
                senderPkScript: Convert.FromHexString(solverRefundPkScript),
                EmulatorCovenantsLegacy.PreTimelockedRefund));
    }

}

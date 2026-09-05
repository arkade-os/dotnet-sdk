using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.ArkadeIntents.SolverRegistry;
using NArk.Core;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>A negotiated on-board, waiting for its L1 HTLC to be funded.</summary>
/// <param name="RfqId">The negotiation, and the swap's id.</param>
/// <param name="Quote">The terms, as accepted.</param>
/// <param name="HtlcAddress">
/// The Bitcoin L1 address to send <paramref name="FundAmountSats"/> to. Fund this and nothing else:
/// it was derived here, not taken from the quote.
/// </param>
/// <param name="FundAmountSats">What the L1 funding must carry, in sats.</param>
/// <param name="HtlcLocktime">Unix seconds at which the L1 refund leaf opens — the way out.</param>
/// <param name="MinConfirmations">Confirmations the solver waits for before it funds Arkade.</param>
/// <param name="LockupAddress">The Arkade covenant the solver will fund, and we will claim.</param>
/// <param name="PaymentHash"><c>sha256(Preimage)</c>, hex.</param>
/// <param name="Preimage">
/// The secret that settles both rails. Kept on the row and re-derivable from the wallet, but worth
/// holding on to: nothing else can move the Arkade lockup.
/// </param>
/// <param name="Contract">The Arkade covenant, derived locally.</param>
/// <param name="PayoutAddress">Our own Arkade address the claim pays out to.</param>
public sealed record PendingOnchainReceive(
    string RfqId,
    RfqQuote<OnchainReceiveQuoteProfile> Quote,
    string HtlcAddress,
    long FundAmountSats,
    long HtlcLocktime,
    int MinConfirmations,
    string LockupAddress,
    string PaymentHash,
    byte[] Preimage,
    VHTLCv2Contract Contract,
    string PayoutAddress);

/// <summary>What a pass over an on-board's L1 refund found.</summary>
/// <param name="Refunded">True when this pass broadcast the refund.</param>
/// <param name="Detail">Why not, when it did not.</param>
/// <param name="Txid">The refund transaction, when one was broadcast.</param>
public sealed record OnchainRefundOutcome(bool Refunded, string? Detail = null, string? Txid = null);

/// <summary>
/// The <c>onchain:BTC-&gt;arkade:BTC</c> corridor: on-board Bitcoin L1 sats into an Arkade balance.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of the off-board in <c>OnchainIntentsClient.cs</c>, and everything that matters is
/// mirrored with it. There we funded Arkade and were repaid on L1; here we fund L1 and are paid on
/// Arkade, which makes the <em>solver</em> the party paying out ahead of being paid. It collects only
/// when our Arkade claim publishes the preimage.
/// </para>
/// <para>
/// We still choose that preimage, for the reason both receive corridors do: whoever is owed the
/// second leg must not be able to release the first on its own.
/// </para>
/// <para>
/// The two deadlines run the other way round too. The solver's Arkade reclaim opens first and closes
/// our claim window; our L1 refund opens last and is the only recourse we have, because on this leg
/// we never funded anything on Arkade to be refunded from.
/// </para>
/// </remarks>
public sealed partial class OnchainIntentsClient
{
    /// <summary>
    /// Negotiate an on-board and verify everything the solver sent back.
    /// </summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="amountSats">The size to ask for, on the leg <paramref name="amountSide"/> names.</param>
    /// <param name="rfqTransport">How to reach the solver.</param>
    /// <param name="covclaimdPubKey">
    /// covclaimd's compressed key, read live from its own endpoint. The preimage is sealed to this so
    /// the Arkade claim can be pushed without us online.
    /// </param>
    /// <param name="l1RefundAddress">
    /// Where the L1 HTLC pays if we have to take it back. Neither contract commits to it, so it is
    /// remembered on the row — a swap whose row is lost can still be refunded once rebuilt, but the
    /// sats land wherever that rebuild names.
    /// </param>
    /// <param name="amountSide">
    /// Which leg <paramref name="amountSats"/> pins, and so who absorbs the solver's spread.
    /// <see cref="RfqAmountSide.From"/> — the default — fixes what we send on L1, which is what a
    /// caller with a particular UTXO to spend means. <see cref="RfqAmountSide.To"/> fixes what lands
    /// on Arkade and leaves the L1 figure to the solver.
    /// </param>
    /// <param name="solverCard">
    /// The solver's published card, when there is one. Supplying it holds the solver to its own
    /// advertised limits and fee.
    /// </param>
    /// <param name="cancellationToken">Cancels the negotiation. Nothing is funded here either way.</param>
    /// <returns>The L1 address to fund, and everything needed to claim once the solver responds.</returns>
    /// <exception cref="RfqRefusedException">The solver declined to quote.</exception>
    /// <exception cref="OnchainReceiveNotFundableException">A safety gate refused — fund nothing.</exception>
    /// <remarks>
    /// Returns rather than funds. The L1 funding transaction is the caller's own wallet's job, exactly
    /// as the off-board leaves its L1 claim destination to the caller: this SDK holds an Arkade
    /// wallet, and the sats being on-boarded are by definition not in it yet.
    /// </remarks>
    public async Task<PendingOnchainReceive> ReceiveFromOnchainAsync(
        string walletId,
        long amountSats,
        IRfqTransport rfqTransport,
        string covclaimdPubKey,
        BitcoinAddress l1RefundAddress,
        RfqAmountSide amountSide = RfqAmountSide.From,
        SolverCard? solverCard = null,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        // One derivation, three roles again — the covenant's `receiver` (so we can claim without
        // covclaimd), the destination its claim leaf is pinned to, and the L1 HTLC's refund key.
        // All on the chain the wallet already recovers, so none of them needs storage to survive.
        var payout = await contractService.DeriveContract(
            walletId, NextContractPurpose.Receive, cancellationToken: cancellationToken);
        var payoutArkAddress = payout.GetArkAddress();
        var payoutPkScript = payoutArkAddress.ScriptPubKey.ToBytes();
        var isMainnet = serverInfo.Network == Network.Main;
        var payoutAddress = payoutArkAddress.ToString(isMainnet);
        var payoutDescriptor = UserKeyOf(payout);
        var clientXOnly = Convert.ToHexString(payoutDescriptor.ToXOnlyPubKey().ToBytes()).ToLowerInvariant();

        // The negotiation id first: for a wallet whose key repeats across swaps it doubles as the
        // preimage salt, so it has to exist before the preimage does.
        var rfqId = RfqProtocol.NewRfqId();
        var preimage = await ProvisionPreimageAsync(walletId, payoutDescriptor, rfqId, cancellationToken);
        var sealed_ = await ClaimPacket.SealAsync(preimage, covclaimdPubKey, _cipher, cancellationToken);

        if (solverCard is not null)
        {
            SolverTerms.AssertWithinLimits(solverCard, OnchainReceiveProfile.Pair, amountSats);
        }

        var request = OnchainReceiveProfile.Request(
            amountSats, amountSide, sealed_.PaymentHash, sealed_.Packet,
            refundPubkey: clientXOnly, payoutAddress, payoutPubkey: clientXOnly, rfqId);

        var quote = await rfqTransport
            .RequestQuoteAsync<OnchainReceiveRequestProfile, OnchainReceiveQuoteProfile>(
                request, cancellationToken);

        if (solverCard is not null)
        {
            SolverTerms.AssertFeeWithinAdvertised(solverCard, quote);
        }

        // Both deadlines, both rails, checked together — the ordering neither contract enforces.
        OnchainReceiveGates.AssertFundable(quote, _time.GetUtcNow().ToUnixTimeSeconds());

        var htlc = DeriveReceiveHtlc(quote, sealed_.PaymentHash, payoutDescriptor, serverInfo.Network);
        if (!string.Equals(htlc.Address.ToString(), quote.Profile?.HtlcAddress, StringComparison.Ordinal))
        {
            throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.IncompleteQuote,
                $"our L1 HTLC derivation is {htlc.Address}, the solver quoted "
                + $"{quote.Profile?.HtlcAddress ?? "(none)"} — refusing to fund an address the two of "
                + "us do not agree on");
        }

        var (eightLeaf, nineLeaf) = DeriveReceiveLockup(
            quote, sealed_.PaymentHash, payoutDescriptor, payoutPkScript, serverInfo);

        var (matched, eightAddress, nineAddress) =
            LightningCorridor.MatchQuotedLockup(eightLeaf, nineLeaf, quote.Profile?.LockupAddress, isMainnet);
        var contract = matched ?? throw new OnchainReceiveNotFundableException(
            OnchainReceiveRefusalReason.IncompleteQuote,
            $"our Arkade lockup derivation is {eightAddress} (eight-leaf) or {nineAddress} (nine-leaf), "
            + $"the solver quoted {quote.Profile?.LockupAddress ?? "(none)"} — refusing an address "
            + "matching neither");
        var lockupAddress = contract.GetArkAddress().ToString(isMainnet);

        // Imported and recorded BEFORE the caller is handed an address to fund. From the moment the
        // L1 funding confirms the solver may fund Arkade, and from then the swap is claimable only by
        // whoever holds the preimage — so the row carrying it has to exist first.
        await contractService.ImportContract(
            walletId, contract, ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"onchain-receive:{rfqId}" },
            cancellationToken: cancellationToken);

        var intent = new ArkadeSwapIntent
        {
            Id = rfqId,
            WalletId = walletId,
            Type = ArkadeSwapIntentType.OnchainToBtc,
            OfferAmount = Money.Satoshis(quote.FromAmount),
            WantAmount = Money.Satoshis(quote.ToAmount),
            // Pending, not Funding: what we are about to fund is on L1, so the Funding status —
            // which means "an Arkade spend of ours is in flight" — would describe nothing.
            Status = ArkadeSwapIntentStatus.Pending,
            CreatedAt = _time.GetUtcNow(),
            SwapPkScript = contract.GetScriptPubKey().ToHex(),
            SwapAddress = lockupAddress,
            FromAssetId = "onchain:btc",
            ToAssetId = "btc",
            PaymentHash = sealed_.PaymentHash,
            // The SOLVER's Arkade reclaim, which on this leg is OUR claim deadline. Our own deadline
            // is the L1 one, and it lives in the metadata beside the key it belongs to.
            RefundLocktime = quote.RefundLocktime,
        }.WithOnchainMetadata(new OnchainSwapMetadata(
            Convert.ToHexString(preimage).ToLowerInvariant(),
            quote.Profile!.ClaimPubkey,
            quote.Profile.HtlcLocktime,
            l1RefundAddress.ToString()));
        await intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        logger?.LogInformation(
            "On-board {RfqId} negotiated: fund {Sats} sats to {Htlc}, lockup {Lockup} pays {Payout}",
            rfqId, quote.FromAmount, htlc.Address, lockupAddress, payoutAddress);

        return new PendingOnchainReceive(
            rfqId, quote, htlc.Address.ToString(), quote.FromAmount,
            quote.Profile.HtlcLocktime!.Value, quote.Profile.MinConfirmations!.Value,
            lockupAddress, sealed_.PaymentHash, preimage, contract, payoutAddress);
    }

    /// <summary>
    /// Derive the L1 HTLC from the quote's own fields plus ours. Roles invert from the off-board:
    /// the solver claims here, we refund.
    /// </summary>
    private static OnchainHtlc DeriveReceiveHtlc(
        RfqQuote<OnchainReceiveQuoteProfile> quote,
        string paymentHash,
        OutputDescriptor clientKey,
        Network network)
    {
        var claimPubkey = quote.Profile?.ClaimPubkey
            ?? throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.IncompleteQuote,
                "the quote carries no claim_pubkey, so the L1 claim leaf cannot be reconstructed");

        return OnchainHtlc.Derive(
// `lendian: false`, and it is load-bearing. The claim leaf commits to
            // RIPEMD160(paymentHash.ToBytes(false)) and the script computes HASH160 over the
            // preimage the witness pushes, so those two agree only when `ToBytes(false)` gives
            // back the raw SHA-256. The byte-array constructor defaults to little-endian and
            // hands them back REVERSED — an address both sides can still derive, funded, and
            // holding a claim leaf no witness can ever satisfy.
            new uint256(Convert.FromHexString(paymentHash), lendian: false),
            ECXOnlyPubKey.Create(Convert.FromHexString(claimPubkey)),
            clientKey.ToXOnlyPubKey(),
            quote.Profile.HtlcLocktime!.Value,
            network);
    }

    /// <summary>
    /// Both Arkade covenant shapes, built exactly as the Lightning receive leg builds its own —
    /// the two receive corridors carry the same covenant, so they derive it the same way.
    /// </summary>
    private (VHTLCv2Contract EightLeaf, VHTLCv2Contract NineLeaf) DeriveReceiveLockup(
        RfqQuote<OnchainReceiveQuoteProfile> quote,
        string paymentHash,
        OutputDescriptor payoutDescriptor,
        byte[] payoutPkScript,
        ArkServerInfo serverInfo)
    {
        var delays = LightningCorridor.UnilateralDelays(serverInfo);

        var solverRefundPkScript = quote.Profile?.SolverRefundPkScript
            ?? throw new OnchainReceiveNotFundableException(
                OnchainReceiveRefusalReason.IncompleteQuote,
                "the quote carries no solver_refund_pk_script, so the covenant's nonInteractiveRefund "
                + "leaf cannot be reconstructed and the lockup address cannot be derived");

        var emulatorPubKey = LightningCorridor.NormalizeToXOnly(
            Convert.FromHexString(EmulatorPubKeys.Resolve(serverInfo.NetworkName, EmulatorPubkeyOverride)));

        return LightningCorridor.DeriveBothLockupShapes(
            serverInfo.SignerKey,
            // The solver funds this one, so it is the covenant's sender and we are its receiver —
            // the exact inversion of the off-board.
            sender: LightningCorridor.DescriptorForXOnly(quote.SolverPubkey, serverInfo.Network),
            receiver: payoutDescriptor,
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(paymentHash)), false),
            new LockTime(checked((uint)quote.RefundLocktime)),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            nonInteractiveClaim: new VHTLCv2NonInteractiveClaim(payoutPkScript, emulatorPubKey),
            refundPkScript: Convert.FromHexString(solverRefundPkScript),
            refundEmulatorPubKey: emulatorPubKey);
    }

    /// <summary>
    /// Take delivery: spend the Arkade lockup the solver funded, revealing the preimage.
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
    /// that is what lets the solver claim our L1 funding. So it is not optional tidy-up in either
    /// direction — leave it unclaimed and the solver reclaims its lockup, after which our only move
    /// is the L1 refund.
    /// </remarks>
    public async Task<ArkadeSwapIntent> ClaimOnchainReceiveAsync(
        string swapId, CancellationToken cancellationToken = default)
    {
        var intent = await intentStorage.GetArkadeSwapIntent(swapId, cancellationToken)
            ?? throw new InvalidOperationException($"Swap '{swapId}' not found.");

        if (intent.Type != ArkadeSwapIntentType.OnchainToBtc)
        {
            throw new InvalidOperationException($"Swap '{swapId}' is not an on-board ({intent.Type}).");
        }
        if (intent.RefundLocktime is not { } locktime)
        {
            throw new InvalidOperationException($"Swap '{swapId}' has no refund locktime recorded.");
        }

        // The claim margin, not the bare deadline. A claim that does not confirm before the solver's
        // reclaim does leaves it with its lockup back AND our preimage out of the mempool — which
        // takes our L1 funding too. Both legs, for the sake of a few minutes.
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (!OnchainReceiveGates.ClaimWindowIsOpen(locktime, now))
        {
            throw new InvalidOperationException(
                $"Swap '{swapId}' has {locktime - now}s before the solver's Arkade reclaim opens, "
                + $"under the {OnchainReceiveGates.ClaimMarginSeconds}s a claim needs to land in; "
                + "the L1 refund is the way out from here.");
        }

        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);
        var contract = await LightningCorridor.LoadLockupAsync(
            contractStorage, intent.SwapPkScript, intent.Id, serverInfo.Network, cancellationToken);

        var preimage = await ResolvePreimageAsync(intent, contract, cancellationToken);

        var vtxos = await vtxoStorage.GetVtxos(
            scripts: [intent.SwapPkScript], cancellationToken: cancellationToken);
        var claimable = LightningIntentsClient.SelectClaimable(
            vtxos, (ulong)intent.WantAmount.Satoshi, swapId);
        var coins = claimable.Select(v => contract.ToClaimCoin(intent.WalletId, v, preimage)).ToArray();
        var total = claimable.Aggregate(0UL, (sum, v) => sum + v.Amount);

        // Where the claim pays was fixed at negotiation time, in the leaf that pins our payout.
        // Reading it back rather than deriving afresh keeps a claim from landing somewhere the swap
        // did not name.
        var claimPkScript = contract.NonInteractiveClaim?.ReceiverPkScript
            ?? throw new InvalidOperationException(
                "the lockup carries no nonInteractiveClaim leaf, so the payout this swap committed "
                + "to cannot be read back");
        var destination = ArkAddress.FromScriptPubKey(
            new Script(claimPkScript), serverInfo.SignerKey.ToXOnlyPubKey());

        var txid = await spendingService.Spend(
            intent.WalletId, coins,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis((long)total), destination)],
            cancellationToken);

        intent.Status = ArkadeSwapIntentStatus.Fulfilled;
        intent.SpentTxid = txid.ToString();
        await intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        logger?.LogInformation("Claimed on-board {SwapId} in {Txid}", swapId, txid);
        return intent;
    }

    /// <summary>
    /// Take back an on-board's L1 funding once its refund leaf has matured.
    /// </summary>
    /// <param name="swapId">The swap's id.</param>
    /// <param name="refundAddress">
    /// Where to pay, overriding the address recorded at negotiation. Omitted, the recorded one is used.
    /// </param>
    /// <param name="cancellationToken">Cancels before the broadcast.</param>
    /// <returns>What the pass found; <see cref="OnchainRefundOutcome.Refunded"/> false is ordinary.</returns>
    /// <exception cref="InvalidOperationException">The swap is unknown, or not an on-board.</exception>
    /// <remarks>
    /// <para>
    /// Called on every advance pass rather than when something says it is time, for the reason the
    /// off-board's claim is: what it waits for is a median time past on a chain no VTXO event
    /// reports. "Not yet" is the normal answer and is not an error.
    /// </para>
    /// <para>
    /// Refused outright once the swap is <see cref="ArkadeSwapIntentStatus.Fulfilled"/>. Past that we
    /// have published the preimage, so the solver can claim this same HTLC — and a refund racing it
    /// is at best a wasted fee and at worst an attempt to take both legs.
    /// </para>
    /// </remarks>
    public async Task<OnchainRefundOutcome> RefundOnchainReceiveAsync(
        string swapId, BitcoinAddress? refundAddress = null, CancellationToken cancellationToken = default)
    {
        var intent = await intentStorage.GetArkadeSwapIntent(swapId, cancellationToken)
            ?? throw new InvalidOperationException($"Swap '{swapId}' is unknown.");

        if (intent.Type != ArkadeSwapIntentType.OnchainToBtc)
        {
            throw new InvalidOperationException($"Swap '{swapId}' is not an on-board.");
        }

        if (intent.Status is ArkadeSwapIntentStatus.Fulfilled or ArkadeSwapIntentStatus.Cancelled)
        {
            return new OnchainRefundOutcome(
                false, $"the swap is {intent.Status}; its L1 leg is not ours to take back");
        }

        var onchain = intent.OnchainMetadata();
        if (onchain.HtlcPubkey is not { Length: > 0 } claimPubkey
            || onchain.HtlcLocktime is not { } htlcLocktime
            || intent.PaymentHash is not { Length: > 0 } paymentHash)
        {
            return new OnchainRefundOutcome(false, "the swap's L1 leg is not recorded on this row");
        }

        var destination = refundAddress
            ?? (onchain.PayoutAddress is { Length: > 0 } recorded
                ? BitcoinAddress.Create(recorded, (await transport.GetServerInfoAsync(cancellationToken)).Network)
                : null);
        if (destination is null)
        {
            return new OnchainRefundOutcome(
                false, "the swap records no L1 refund address and none was supplied");
        }

        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);
        var network = serverInfo.Network;

        // The imported contract row is where our key lives once the negotiation is behind us — the
        // covenant's `receiver` on this leg, which is the same key the L1 refund leaf commits to.
        var contract = await LightningCorridor.LoadLockupAsync(
            contractStorage, intent.SwapPkScript, intent.Id, network, cancellationToken);
        var clientKey = contract.Receiver;

        var htlc = OnchainHtlc.Derive(
// `lendian: false`, and it is load-bearing. The claim leaf commits to
            // RIPEMD160(paymentHash.ToBytes(false)) and the script computes HASH160 over the
            // preimage the witness pushes, so those two agree only when `ToBytes(false)` gives
            // back the raw SHA-256. The byte-array constructor defaults to little-endian and
            // hands them back REVERSED — an address both sides can still derive, funded, and
            // holding a claim leaf no witness can ever satisfy.
            new uint256(Convert.FromHexString(paymentHash), lendian: false),
            ECXOnlyPubKey.Create(Convert.FromHexString(claimPubkey)),
            clientKey.ToXOnlyPubKey(),
            htlcLocktime,
            network);

        var utxos = await blockchain.GetUtxosAsync(htlc.Address.ToString(), cancellationToken);
        var live = utxos.Where(u => u.Confirmed).ToList();
        if (live.Count == 0)
        {
            return new OnchainRefundOutcome(
                false, "the L1 HTLC holds nothing confirmed — either it was never funded, or it is gone");
        }

        // Median time past, never the local clock: consensus matures CLTV against it, and it trails
        // wall clock by about an hour. A refund built against the wrong clock is well formed and
        // rejected as non-final, with nothing in the rejection saying why.
        var chain = await blockchain.GetChainTime(cancellationToken);
        var mtp = chain.Timestamp.ToUnixTimeSeconds();
        if (!OnchainReceiveGates.RefundIsDue(htlcLocktime, mtp))
        {
            return new OnchainRefundOutcome(
                false, $"the L1 refund leaf opens at {htlcLocktime}; the chain's median time past is {mtp}");
        }

        var signer = await walletProvider.GetSignerAsync(intent.WalletId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Wallet '{intent.WalletId}' cannot sign, so its L1 refund cannot be built.");
        var feeRate = await blockchain.EstimateFeeRateAsync(cancellationToken: cancellationToken);

        var signed = await OnchainRefundBuilder.BuildAsync(
            htlc, live, destination, feeRate,
            async hash => (await signer.Sign(clientKey, hash, cancellationToken)).Item2,
            cancellationToken);

        if (!await blockchain.BroadcastAsync(signed, cancellationToken))
        {
            return new OnchainRefundOutcome(false, "the refund transaction was not accepted");
        }

        var total = live.Aggregate(0UL, (sum, u) => sum + u.Amount);
        intent.Status = ArkadeSwapIntentStatus.Cancelled;
        await intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        logger?.LogInformation(
            "Swap {SwapId}: refunded {Sats} sats on L1 in {Txid}", swapId, total, signed.GetHash());

        return new OnchainRefundOutcome(true, Txid: signed.GetHash().ToString());
    }

    /// <summary>
    /// The preimage for a claim: the row's copy, or rebuilt from the wallet when the row has none.
    /// </summary>
    /// <remarks>
    /// Both inputs survive independently of the secret — the claim key is the covenant's own
    /// <c>receiver</c>, read back off the contract, and the salt is the swap's id — so a row restored
    /// from a backup that never held a preimage can still produce one. It is proven against the
    /// covenant's hash before it is used: a wrong derivation makes a witness the script rejects, and
    /// discovering that at broadcast means discovering it with the claim window already spent.
    /// </remarks>
    private async Task<byte[]> ResolvePreimageAsync(
        ArkadeSwapIntent intent, VHTLCv2Contract contract, CancellationToken cancellationToken)
    {
        if (intent.OnchainMetadata().Preimage is { Length: > 0 } stored)
        {
            return Convert.FromHexString(stored);
        }

        var signer = await walletProvider.GetSignerAsync(intent.WalletId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Swap '{intent.Id}' has no stored preimage and its wallet cannot sign, so the "
                + "secret that claims it cannot be rebuilt.");

        var salt = PreimageProvisioning.IsPerArtifactDescriptor(contract.Receiver)
            ? null
            : Convert.FromHexString(intent.Id);
        var preimage = await PreimageProvisioning.DerivePreimageAsync(
            signer, contract.Receiver, salt, cancellationToken);

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
}

using BTCPayServer.Lightning;
using NArk.Abstractions;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

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
/// The mirror of <see cref="LightningSwapClient"/>, and the exposure mirrors with it. Here the
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
public sealed class LightningReceiveClient
{
    private readonly IClientTransport _transport;
    private readonly IEmulatorProvider _emulator;
    private readonly IContractService _contractService;
    private readonly ISpendingService _spendingService;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly TimeProvider _time;
    private readonly ILogger<LightningReceiveClient>? _logger;

    /// <summary>Creates the client.</summary>
    /// <param name="transport">The Arkade connection — the source of the server key and its exit delay.</param>
    /// <param name="emulator">The covenant co-signer, fetched from the client's own endpoint.</param>
    /// <param name="contractService">Derives the client's own payout address and imports the lockup.</param>
    /// <param name="spendingService">Spends the claim.</param>
    /// <param name="intentStorage">Records the swap — including the preimage, which lives nowhere else.</param>
    /// <param name="contractStorage">Source of the imported lockup contract, for the claim path.</param>
    /// <param name="vtxoStorage">Source of the lockup VTXO the solver funded.</param>
    /// <param name="time">Clock for the claim's deadline check; defaults to the system clock.</param>
    /// <param name="logger">Optional logger.</param>
    public LightningReceiveClient(
        IClientTransport transport,
        IEmulatorProvider emulator,
        IContractService contractService,
        ISpendingService spendingService,
        IArkadeIntentStorage intentStorage,
        IContractStorage contractStorage,
        IVtxoStorage vtxoStorage,
        TimeProvider? time = null,
        ILogger<LightningReceiveClient>? logger = null)
    {
        _transport = transport;
        _emulator = emulator;
        _contractService = contractService;
        _spendingService = spendingService;
        _intentStorage = intentStorage;
        _contractStorage = contractStorage;
        _vtxoStorage = vtxoStorage;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Negotiate a receive swap and verify everything the solver sent back.
    /// </summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="amountSats">What to receive on Arkade, in sats.</param>
    /// <param name="rfqTransport">How to reach the solver.</param>
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
        var payoutDescriptor = payout is ArkPaymentContract payment
            ? payment.User
            : throw new InvalidOperationException(
                $"expected a payment contract to take the payout key from, got {payout.GetType().Name}");

        var sealed_ = ClaimPacket.New(covclaimdPubKey);

        var request = LightningReceiveProfile.Request(
            amountSats,
            sealed_.PaymentHash,
            payoutAddress,
            Convert.ToHexString(payoutDescriptor.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
            sealed_.Packet);

        var quote = await rfqTransport
            .RequestQuoteAsync<LightningReceiveRequestProfile, LightningReceiveQuoteProfile>(
                request, cancellationToken);

        var invoice = LightningReceiveGates.VerifyInvoice(quote, sealed_.PaymentHash, serverInfo.Network);

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
        var intent = (await _intentStorage.GetArkadeSwapIntents(cancellationToken: cancellationToken))
                         .FirstOrDefault(s => s.Id == swapId)
                     ?? throw new InvalidOperationException($"Swap '{swapId}' not found.");

        if (intent.Type != ArkadeSwapIntentType.LightningToBtc)
            throw new InvalidOperationException($"Swap '{swapId}' is not a Lightning receive ({intent.Type}).");
        if (intent.Preimage is not { Length: > 0 } preimageHex)
            throw new InvalidOperationException($"Swap '{swapId}' has no preimage recorded — it cannot be claimed.");
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

        var vtxos = await _vtxoStorage.GetVtxos(
            scripts: [intent.SwapPkScript], cancellationToken: cancellationToken);
        var vtxo = vtxos.FirstOrDefault(v => !v.IsSpent() && !v.Swept)
            ?? throw new InvalidOperationException(
                $"Swap '{swapId}' has no unspent lockup — the solver has not funded it yet.");

        var coin = contract.ToClaimCoin(intent.WalletId, vtxo, Convert.FromHexString(preimageHex));

        // Where the claim pays was fixed at negotiation time, in the leaf that pins our payout.
        // Reading it back rather than deriving afresh keeps a claim from ever landing somewhere the
        // swap did not name.
        var destination = ArkAddress.FromScriptPubKey(
            new Script(contract.NonInteractiveClaimPkScript), serverInfo.SignerKey.ToXOnlyPubKey());
        var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis((long)vtxo.Amount), destination);

        var txid = await _spendingService.Spend(intent.WalletId, [coin], [output], cancellationToken);

        intent.Status = ArkadeSwapIntentStatus.Fulfilled;
        intent.SpentTxid = txid.ToString();
        await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        _logger?.LogInformation("Claimed Lightning receive swap {SwapId} in {Txid}", swapId, txid);
        return intent;
    }

    /// <summary>
    /// Build the funding contract from the quote's binding fields and the client's own data.
    /// </summary>
    /// <remarks>
    /// Roles invert here relative to the send leg: the solver funds, so it is the covenant's
    /// <c>sender</c>, and the client claims, so it is the <c>receiver</c>. The two covenant payout
    /// destinations follow them — <c>nonInteractiveClaim</c> pays the client, <c>nonInteractiveRefund</c>
    /// pays the solver.
    /// </remarks>
    private async Task<VHTLCv2Contract> DeriveLockupAsync(
        RfqQuote<LightningReceiveQuoteProfile> quote,
        string paymentHash,
        OutputDescriptor payoutDescriptor,
        byte[] payoutPkScript,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        var emulatorInfo = await _emulator.GetInfoAsync(cancellationToken);
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
            LightningCorridor.NormalizeToXOnly(Convert.FromHexString(emulatorInfo.SignerPubkey)),
            nonInteractiveClaimPkScript: payoutPkScript,
            nonInteractiveRefundPkScript: Convert.FromHexString(solverRefundPkScript));
    }

}

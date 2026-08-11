using BTCPayServer.Lightning;
using NArk.Abstractions;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.Core;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;

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
public sealed record FundedLightningSwap(
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
public sealed class LightningSwapClient
{
    private readonly IClientTransport _transport;
    private readonly IEmulatorProvider _emulator;
    private readonly IContractService _contractService;
    private readonly ISpendingService _spendingService;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IWalletProvider _walletProvider;
    private readonly TimeProvider _time;
    private readonly ILogger<LightningSwapClient>? _logger;

    /// <summary>Creates the client.</summary>
    /// <param name="transport">The Arkade connection — the source of the server key and its exit delay.</param>
    /// <param name="emulator">The covenant co-signer, fetched from the maker's own endpoint.</param>
    /// <param name="contractService">Derives the maker's own receive address for the refund destination.</param>
    /// <param name="spendingService">Funds the lockup.</param>
    /// <param name="intentStorage">Records the swap so it survives a restart.</param>
    /// <param name="contractStorage">Source of the imported lockup contract, for the refund path.</param>
    /// <param name="vtxoStorage">Source of the lockup VTXO to refund.</param>
    /// <param name="walletProvider">Needed by the program transformer on the refund path.</param>
    /// <param name="time">Clock for the funding and refund gates; defaults to the system clock.</param>
    /// <param name="logger">Optional logger.</param>
    public LightningSwapClient(
        IClientTransport transport,
        IEmulatorProvider emulator,
        IContractService contractService,
        ISpendingService spendingService,
        IArkadeIntentStorage intentStorage,
        IContractStorage contractStorage,
        IVtxoStorage vtxoStorage,
        IWalletProvider walletProvider,
        TimeProvider? time = null,
        ILogger<LightningSwapClient>? logger = null)
    {
        _transport = transport;
        _emulator = emulator;
        _contractService = contractService;
        _spendingService = spendingService;
        _intentStorage = intentStorage;
        _contractStorage = contractStorage;
        _vtxoStorage = vtxoStorage;
        _walletProvider = walletProvider;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

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
    /// <exception cref="LightningSwapNotFundableException">A safety gate refused — nothing was funded.</exception>
    public async Task<FundedLightningSwap> SendToLightningAsync(
        string walletId,
        string invoice,
        IRfqTransport rfqTransport,
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
        var quote = await rfqTransport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(
            request, cancellationToken);

        var contract = await DeriveLockupAsync(
            quote, decoded, refundPkScript, clientRefund, serverInfo, cancellationToken);
        var lockupArkAddress = contract.GetArkAddress();
        var lockupAddress = lockupArkAddress.ToString(serverInfo.Network == Network.Main);

        LightningSwapGates.VerifyLockupAddress(quote, lockupAddress);

        // Checked here, immediately before the irreversible step — not when the quote arrived.
        LightningSwapGates.AssertFundable(
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

        return new FundedLightningSwap(
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
    public async Task<ArkadeSwapIntent> RefundSwap(string swapId, CancellationToken cancellationToken = default)
    {
        var intent = (await _intentStorage.GetArkadeSwapIntents(cancellationToken: cancellationToken))
                         .FirstOrDefault(s => s.Id == swapId)
                     ?? throw new InvalidOperationException($"Swap '{swapId}' not found.");

        if (intent.Type != ArkadeSwapIntentType.BtcToLightning)
            throw new InvalidOperationException($"Swap '{swapId}' is not a Lightning swap ({intent.Type}).");
        if (intent.Status is not (ArkadeSwapIntentStatus.Refundable or ArkadeSwapIntentStatus.Pending))
            throw new InvalidOperationException($"Swap '{swapId}' is not awaiting a refund (status {intent.Status}).");
        if (intent.RefundLocktime is not { } locktime)
            throw new InvalidOperationException($"Swap '{swapId}' has no refund locktime recorded.");

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (now < locktime)
        {
            throw new InvalidOperationException(
                $"Swap '{swapId}' cannot be refunded for another {locktime - now}s.");
        }

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
            var vtxo = vtxos.FirstOrDefault(v => !v.IsSpent() && !v.Swept)
                ?? throw new InvalidOperationException("no unspent VTXO at the swap address");

            // `refundWithoutReceiver`: our own key plus the server, once the locktime checked above
            // has passed. Deliberately not the faster `nonInteractiveRefund` leaf — that one needs
            // the solver's signature, so it is a path the two of us take by agreement, not one we
            // can take alone. This is the exit that depends on no counterparty.
            var coin = contract.ToRefundWithoutReceiverCoin(intent.WalletId, vtxo);

            // This leaf carries no covenant, so nothing on-chain pins the payout — we choose it. It
            // still goes to the address committed at funding time, read back off the contract rather
            // than derived afresh, so a refund can never land somewhere the swap never named.
            var destination = RefundAddressOf(contract, serverInfo.SignerKey.ToXOnlyPubKey());
            var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis((long)vtxo.Amount), destination);

            var txid = await _spendingService.Spend(intent.WalletId, [coin], [output], cancellationToken);

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

    /// <summary>
    /// Build the swap contract from the quote's binding fields and the maker's own data.
    /// </summary>
    /// <param name="quote">The solver's quote — only its binding fields are read.</param>
    /// <param name="invoice">The decoded invoice, the source of the payment hash.</param>
    /// <param name="refundPkScript">The maker's own P2TR scriptPubKey.</param>
    /// <param name="clientRefund">The maker's own key for the covenant's client-side leaves.</param>
    /// <param name="serverInfo">The Arkade server's own terms.</param>
    /// <param name="cancellationToken">Cancels the emulator fetch.</param>
    /// <returns>The derived contract.</returns>
    /// <remarks>
    /// The one value taken from the quote's non-binding half is
    /// <see cref="LightningSendQuoteProfile.ReceiverPkScript"/>, and only because every leaf feeds
    /// the merkle root: without the solver's own claim destination there is no way to reproduce the
    /// address at all. It is safe to take because that leaf pays the solver — a wrong value costs
    /// the solver a spending path and the maker nothing.
    /// </remarks>
    private async Task<VHTLCv2Contract> DeriveLockupAsync(
        RfqQuote<LightningSendQuoteProfile> quote,
        BOLT11PaymentRequest invoice,
        byte[] refundPkScript,
        OutputDescriptor clientRefund,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        var emulatorInfo = await _emulator.GetInfoAsync(cancellationToken);
        var delays = LightningCorridor.UnilateralDelays(serverInfo);

        var receiverPkScript = quote.Profile?.ReceiverPkScript
            ?? throw new InvalidOperationException(
                "the quote carries no receiver_pk_script, so the covenant's nonInteractiveClaim leaf " +
                "cannot be reconstructed and the lockup address cannot be derived");

        return new VHTLCv2Contract(
            serverInfo.SignerKey,
            // Roles are positional: on this corridor the maker sends and the solver receives.
            sender: clientRefund,
            receiver: LightningCorridor.DescriptorForXOnly(quote.SolverPubkey, serverInfo.Network),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(invoice.PaymentHash.ToBytes()), false),
            new LockTime(checked((uint)quote.RefundLocktime)),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            LightningCorridor.NormalizeToXOnly(Convert.FromHexString(emulatorInfo.SignerPubkey)),
            nonInteractiveClaimPkScript: Convert.FromHexString(receiverPkScript),
            nonInteractiveRefundPkScript: refundPkScript);
    }

    /// <summary>
    /// The wallet-owned descriptor behind a derived receive contract — the key the covenant's
    /// client-side refund leaves are built around, and the one that signs them.
    /// </summary>
    private static OutputDescriptor ClientRefundDescriptorOf(ArkContract receive) =>
        receive is ArkPaymentContract payment
            ? payment.User
            : throw new InvalidOperationException(
                $"expected a payment contract to take the client refund key from, got {receive.GetType().Name}");
}

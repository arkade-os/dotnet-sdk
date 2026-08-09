using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
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
/// <b>Not yet reachable end to end.</b> The reference solver implements this corridor but does not
/// route it on any transport — its RFQ ingress dispatches only the two send pairs — so this class
/// has no counterparty to negotiate with until that lands.
/// </para>
/// </remarks>
public sealed class LightningReceiveClient
{
    private readonly IClientTransport _transport;
    private readonly IEmulatorProvider _emulator;
    private readonly IContractService _contractService;
    private readonly ILogger<LightningReceiveClient>? _logger;

    /// <summary>Creates the client.</summary>
    /// <param name="transport">The Arkade connection — the source of the server key and its exit delay.</param>
    /// <param name="emulator">The covenant co-signer, fetched from the client's own endpoint.</param>
    /// <param name="contractService">Derives the client's own payout address.</param>
    /// <param name="logger">Optional logger.</param>
    public LightningReceiveClient(
        IClientTransport transport,
        IEmulatorProvider emulator,
        IContractService contractService,
        ILogger<LightningReceiveClient>? logger = null)
    {
        _transport = transport;
        _emulator = emulator;
        _contractService = contractService;
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

        _logger?.LogInformation(
            "Receive swap {RfqId} negotiated: {Amount} sats to {Payout}, lockup {Lockup}",
            request.RfqId, amountSats, payoutAddress, lockupAddress);

        return new PendingLightningReceive(
            request.RfqId, quote, invoice.ToString(), sealed_.Preimage, sealed_.PaymentHash,
            contract, lockupAddress, payoutAddress);
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
        var delays = UnilateralDelaysFor(serverInfo);

        var solverRefundPkScript = quote.Profile?.SolverRefundPkScript
            ?? throw new InvalidOperationException(
                "the quote carries no solver_refund_pk_script, so the covenant's nonInteractiveRefund " +
                "leaf cannot be reconstructed and the lockup address cannot be derived");

        return new VHTLCv2Contract(
            serverInfo.SignerKey,
            sender: DescriptorForXOnly(quote.SolverPubkey, serverInfo.Network),
            receiver: payoutDescriptor,
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(paymentHash)), false),
            new LockTime(checked((uint)quote.RefundLocktime)),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            NormalizeToXOnly(Convert.FromHexString(emulatorInfo.SignerPubkey)),
            nonInteractiveClaimPkScript: payoutPkScript,
            nonInteractiveRefundPkScript: Convert.FromHexString(solverRefundPkScript));
    }

    /// <summary>
    /// The three CSV delays, derived from the server's own minimum exit delay exactly as the
    /// counterparty derives them — they are deliberately not carried on the wire.
    /// </summary>
    private static (uint Claim, uint Refund, uint RefundWithoutReceiver) UnilateralDelaysFor(
        ArkServerInfo serverInfo)
    {
        var exit = serverInfo.UnilateralExit;
        if (exit.LockType != SequenceLockType.Time)
        {
            throw new InvalidOperationException(
                "the Arkade server advertises its unilateral exit delay in blocks; this swap script " +
                "encodes a time-based delay, and block-interval variance is far too wide to hold a " +
                "Lightning HTLC deadline against");
        }

        var claim = SwapScriptValues.CeilToGranularity(checked((uint)exit.LockPeriod.TotalSeconds));
        return (claim,
            claim + SwapScriptValues.SequenceGranularitySeconds,
            claim + 2 * SwapScriptValues.SequenceGranularitySeconds);
    }

    private static ECXOnlyPubKey NormalizeToXOnly(byte[] pubkey) =>
        ECXOnlyPubKey.Create(pubkey.Length == 33 ? pubkey[1..] : pubkey);

    private static OutputDescriptor DescriptorForXOnly(string xOnlyHex, Network network) =>
        KeyExtensions.ParseOutputDescriptor("02" + xOnlyHex.ToLowerInvariant(), network);
}

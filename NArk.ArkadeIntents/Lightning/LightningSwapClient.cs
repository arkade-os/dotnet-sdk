using BTCPayServer.Lightning;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Lightning.Rfq;
using NArk.ArkadeIntents.Models;
using NArk.Core;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

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
    RfqQuote Quote,
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
    private readonly TimeProvider _time;

    /// <summary>Creates the client.</summary>
    /// <param name="transport">The Arkade connection — the source of the server key and its exit delay.</param>
    /// <param name="emulator">The covenant co-signer, fetched from the maker's own endpoint.</param>
    /// <param name="contractService">Derives the maker's own receive address for the refund destination.</param>
    /// <param name="spendingService">Funds the lockup.</param>
    /// <param name="intentStorage">Records the swap so it survives a restart.</param>
    /// <param name="time">Clock for the funding gates; defaults to the system clock.</param>
    public LightningSwapClient(
        IClientTransport transport,
        IEmulatorProvider emulator,
        IContractService contractService,
        ISpendingService spendingService,
        IArkadeIntentStorage intentStorage,
        TimeProvider? time = null)
    {
        _transport = transport;
        _emulator = emulator;
        _contractService = contractService;
        _spendingService = spendingService;
        _intentStorage = intentStorage;
        _time = time ?? TimeProvider.System;
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

        var request = RfqRequest.ForSend(invoice, refundAddress);
        var quote = await rfqTransport.RequestQuoteAsync(request, cancellationToken);

        var contract = await DeriveLockupAsync(quote, decoded, refundPkScript, serverInfo, cancellationToken);
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

        var txid = await _spendingService.Spend(
            walletId,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(quote.FromAmount), lockupArkAddress)],
            cancellationToken);

        await _intentStorage.SaveArkadeSwapIntent(new ArkadeSwapIntent
        {
            Id = request.RfqId,
            WalletId = walletId,
            Type = ArkadeSwapIntentType.BtcToLightning,
            OfferAmount = Money.Satoshis(quote.FromAmount),
            WantAmount = Money.Satoshis(quote.ToAmount),
            Status = ArkadeSwapIntentStatus.Pending,
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
        }, cancellationToken);

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
    /// Build the swap contract from the quote's binding fields and the maker's own data.
    /// </summary>
    /// <param name="quote">The solver's quote — only its binding fields are read.</param>
    /// <param name="invoice">The decoded invoice, the source of the payment hash.</param>
    /// <param name="refundPkScript">The maker's own P2TR scriptPubKey.</param>
    /// <param name="serverInfo">The Arkade server's own terms.</param>
    /// <param name="cancellationToken">Cancels the emulator fetch.</param>
    /// <returns>The compiled contract.</returns>
    private async Task<ArkProgramContract> DeriveLockupAsync(
        RfqQuote quote,
        BOLT11PaymentRequest invoice,
        byte[] refundPkScript,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        var emulatorInfo = await _emulator.GetInfoAsync(cancellationToken);

        var parameters = new CovenantSwapParams(
            Receiver: Convert.FromHexString(quote.SolverPubkey),
            PreimageHash: CovenantSwapProgram.PreimageHashFromPaymentHash(invoice.PaymentHash.ToBytes()),
            RefundLocktime: checked((uint)quote.RefundLocktime),
            ClaimDelay: ClaimDelayFor(serverInfo),
            EmulatorPubkey: Convert.FromHexString(emulatorInfo.SignerPubkey),
            RefundPkScript: refundPkScript);

        return CovenantSwapProgram.BuildContract(parameters, serverInfo.SignerKey);
    }

    /// <summary>
    /// Derive the solver's unilateral-claim delay from the server's own minimum exit delay.
    /// </summary>
    /// <param name="serverInfo">The server's advertised terms.</param>
    /// <returns>The delay in seconds, rounded up to a whole BIP68 unit.</returns>
    /// <remarks>
    /// This cannot be a constant. The server rejects any script whose exit delay is below its
    /// configured minimum, and that minimum differs by orders of magnitude between deployments —
    /// thousands of seconds on a test network against roughly a week on mainnet. Worse, the
    /// rejection surfaces only when a spend is attempted: funding is accepted first, so a wrong
    /// constant fails once there is already money in the script.
    /// </remarks>
    private static uint ClaimDelayFor(ArkServerInfo serverInfo)
    {
        var exit = serverInfo.UnilateralExit;
        if (exit.LockType != SequenceLockType.Time)
        {
            throw new InvalidOperationException(
                "the Arkade server advertises its unilateral exit delay in blocks; this swap script " +
                "encodes a time-based delay, and block-interval variance is far too wide to hold a " +
                "Lightning HTLC deadline against");
        }
        return CovenantSwapProgram.CeilToGranularity(checked((uint)exit.LockPeriod.TotalSeconds));
    }
}

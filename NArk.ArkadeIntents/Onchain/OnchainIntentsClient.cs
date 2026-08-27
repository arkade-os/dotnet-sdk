using System.Security.Cryptography;
using NArk.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.ArkadeIntents.SolverRegistry;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>The result of funding an off-board's Arkade side.</summary>
/// <param name="RfqId">The negotiation, and the swap's id.</param>
/// <param name="Quote">The terms funded against.</param>
/// <param name="LockupAddress">The Arkade covenant the client funded.</param>
/// <param name="HtlcAddress">The L1 address the solver must fund, and the client then claims.</param>
/// <param name="PaymentHash">The hash both legs turn on.</param>
/// <param name="FundedSats">What left the Arkade balance.</param>
/// <param name="FundingTxid">The Arkade spend that funded the lockup.</param>
public sealed record FundedOnchainSend(
    string RfqId,
    RfqQuote<OnchainSendQuoteProfile> Quote,
    string LockupAddress,
    string HtlcAddress,
    string PaymentHash,
    long FundedSats,
    string FundingTxid);

/// <summary>
/// The <c>arkade:BTC-&gt;onchain:BTC</c> corridor: off-board an Arkade balance to Bitcoin L1.
/// </summary>
/// <remarks>
/// <para>
/// Two contracts, two chains, one secret. The client funds an Arkade covenant the solver can only
/// take by publishing a preimage; the solver funds an L1 HTLC only that preimage releases. The
/// client holds it, so the client is paid first and the solver is paid by the act of being paid.
/// </para>
/// <para>
/// Nothing here trusts the solver's account of anything. Both addresses are rebuilt locally and
/// compared against the quote's rendering of them, the L1 funding is read off the chain rather than
/// reported, and the preimage is derived from the wallet's own key so a lost record does not lose
/// the claim.
/// </para>
/// </remarks>
public sealed class OnchainIntentsClient(
    IClientTransport transport,
    IContractService contractService,
    ISpendingService spendingService,
    IArkadeIntentStorage intentStorage,
    IVtxoStorage vtxoStorage,
    IWalletProvider walletProvider,
    IBitcoinBlockchain blockchain,
    IOptions<ArkadeIntentsOptions>? options = null,
    TimeProvider? time = null,
    ILogger<OnchainIntentsClient>? logger = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string? _emulatorPubkeyOverride =
        (options?.Value ?? new ArkadeIntentsOptions()).EmulatorPubkeyOverride;

    /// <summary>
    /// Negotiate an off-board and fund its Arkade side.
    /// </summary>
    /// <param name="walletId">The wallet paying, and receiving both the payout and any refund.</param>
    /// <param name="amountSats">The size, on the leg <paramref name="amountSide"/> names.</param>
    /// <param name="amountSide">Which leg the size pins.</param>
    /// <param name="rfqTransport">How to reach the solver.</param>
    /// <param name="solverCard">The solver's registry card, to hold it to its published terms.</param>
    /// <param name="cancellationToken">Cancels before funding; after funding the swap is live regardless.</param>
    /// <returns>The funded swap.</returns>
    /// <exception cref="RfqRefusedException">The solver declined to quote.</exception>
    /// <exception cref="OnchainSendNotFundableException">A safety gate refused — nothing was funded.</exception>
    public async Task<FundedOnchainSend> SendToOnchainAsync(
        string walletId,
        long amountSats,
        RfqAmountSide amountSide,
        IRfqTransport rfqTransport,
        SolverCard? solverCard = null,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        // One derivation serves three roles: the Arkade refund destination, the key the covenant's
        // client-side leaves are built around, and the key that claims on L1. Deliberately the same
        // chain the wallet already recovers, so none of the three needs storage to survive.
        var receive = await contractService.DeriveContract(
            walletId, NextContractPurpose.Receive, cancellationToken: cancellationToken);
        var refundArkAddress = receive.GetArkAddress();
        var clientKey = UserKeyOf(receive);
        var refundAddress = refundArkAddress.ToString(serverInfo.Network == Network.Main);
        var clientXOnly = Convert.ToHexString(clientKey.ToXOnlyPubKey().ToBytes()).ToLowerInvariant();

        var rfqId = RfqProtocol.NewRfqId();
        var preimage = await ProvisionPreimageAsync(walletId, clientKey, rfqId, cancellationToken);
        var paymentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(preimage)).ToLowerInvariant();

        if (solverCard is not null)
        {
            SolverTerms.AssertWithinLimits(solverCard, OnchainSendProfile.Pair, amountSats);
        }

        var request = OnchainSendProfile.Request(
            amountSats, amountSide, paymentHash, clientXOnly, refundAddress, clientXOnly, rfqId);

        var quote = await rfqTransport
            .RequestQuoteAsync<OnchainSendRequestProfile, OnchainSendQuoteProfile>(request, cancellationToken);

        if (solverCard is not null)
        {
            SolverTerms.AssertFeeWithinAdvertised(solverCard, quote);
        }

        // Both deadlines, both chains, checked together — this is where the ordering that neither
        // contract enforces is enforced.
        OnchainSendGates.AssertFundable(quote, _time.GetUtcNow().ToUnixTimeSeconds());

        var htlc = DeriveHtlc(quote, preimage, clientKey, serverInfo.Network);
        AssertMatches(htlc.Address.ToString(), quote.Profile?.HtlcAddress, "L1 HTLC");

        var contract = await DeriveLockupAsync(
            quote, paymentHash, refundArkAddress.ScriptPubKey.ToBytes(), clientKey, serverInfo);
        var lockupArkAddress = contract.GetArkAddress();
        var lockupAddress = lockupArkAddress.ToString(serverInfo.Network == Network.Main);
        AssertMatches(lockupAddress, quote.Profile?.LockupAddress, "Arkade lockup");

        // Imported before funding, recorded before the spend — the same ordering the other corridors
        // keep, and for the same reason: a crash between the money moving and the record existing is
        // the one failure here with no way back.
        await contractService.ImportContract(
            walletId, contract, ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"onchain-swap:{rfqId}" },
            cancellationToken: cancellationToken);

        var intent = new ArkadeSwapIntent
        {
            Id = rfqId,
            WalletId = walletId,
            Type = ArkadeSwapIntentType.BtcToOnchain,
            OfferAmount = Money.Satoshis(quote.FromAmount),
            WantAmount = Money.Satoshis(quote.ToAmount),
            Status = ArkadeSwapIntentStatus.Funding,
            CreatedAt = _time.GetUtcNow(),
            SwapPkScript = lockupArkAddress.ScriptPubKey.ToHex(),
            SwapAddress = lockupAddress,
            OfferHex = "",
            FromAssetId = "btc",
            ToAssetId = "onchain:btc",
            PaymentHash = paymentHash,
            Preimage = Convert.ToHexString(preimage).ToLowerInvariant(),
            RefundLocktime = quote.RefundLocktime,
            // The L1 half the claim pass needs. Only these two — the address is recomputed from
            // them, so it cannot drift from what derived it.
            HtlcPubkey = quote.Profile!.HtlcPubkey,
            HtlcLocktime = quote.Profile.HtlcLocktime,
        };
        await intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        var txid = await spendingService.Spend(
            walletId,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(quote.FromAmount), lockupArkAddress)],
            cancellationToken);

        intent.Status = ArkadeSwapIntentStatus.Pending;
        await intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

        return new FundedOnchainSend(
            rfqId, quote, lockupAddress, htlc.Address.ToString(), paymentHash,
            quote.FromAmount, txid.ToString());
    }

    /// <summary>
    /// Derive the L1 HTLC from the quote's own fields plus the client's.
    /// </summary>
    /// <remarks>
    /// The solver contributes only its refund key and the locktime; everything that decides who gets
    /// paid is the client's. A wrong contribution from the solver produces an address that does not
    /// match its own quote, which is refused before anything is funded.
    /// </remarks>
    private static OnchainHtlc DeriveHtlc(
        RfqQuote<OnchainSendQuoteProfile> quote, byte[] preimage, OutputDescriptor clientKey, Network network)
    {
        var htlcPubkey = quote.Profile?.HtlcPubkey
            ?? throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.IncompleteQuote,
                "the quote carries no htlc_pubkey, so the L1 refund leaf cannot be reconstructed");

        return OnchainHtlc.Derive(
            new uint256(System.Security.Cryptography.SHA256.HashData(preimage)),
            clientKey.ToXOnlyPubKey(),
            ECXOnlyPubKey.Create(Convert.FromHexString(htlcPubkey)),
            quote.Profile.HtlcLocktime!.Value,
            network);
    }

    private static void AssertMatches(string derived, string? quoted, string what)
    {
        if (!string.Equals(derived, quoted, StringComparison.Ordinal))
        {
            throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.IncompleteQuote,
                $"our {what} derivation is {derived}, the solver quoted {quoted ?? "(none)"} — "
                + "refusing to fund an address the two of us do not agree on");
        }
    }

    /// <summary>The Arkade covenant, built exactly as the Lightning send leg builds its own.</summary>
    private async Task<VHTLCv2Contract> DeriveLockupAsync(
        RfqQuote<OnchainSendQuoteProfile> quote,
        string paymentHash,
        byte[] refundPkScript,
        OutputDescriptor clientKey,
        ArkServerInfo serverInfo)
    {
        var delays = LightningCorridor.UnilateralDelays(serverInfo);
        var receiverPkScript = quote.Profile?.ReceiverPkScript
            ?? throw new OnchainSendNotFundableException(
                OnchainSendRefusalReason.IncompleteQuote,
                "the quote carries no receiver_pk_script, so the covenant's nonInteractiveClaim leaf "
                + "cannot be reconstructed and the lockup address cannot be derived");

        return await Task.FromResult(new VHTLCv2Contract(
            serverInfo.SignerKey,
            sender: clientKey,
            receiver: LightningCorridor.DescriptorForXOnly(quote.SolverPubkey, serverInfo.Network),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(paymentHash)), false),
            new LockTime(checked((uint)quote.RefundLocktime)),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            LightningCorridor.NormalizeToXOnly(
                Convert.FromHexString(EmulatorPubKeys.Resolve(serverInfo.NetworkName, _emulatorPubkeyOverride))),
            nonInteractiveClaimPkScript: Convert.FromHexString(receiverPkScript),
            nonInteractiveRefundPkScript: refundPkScript));
    }

    /// <summary>The preimage, derived from the wallet so a lost record does not lose the claim.</summary>
    private async Task<byte[]> ProvisionPreimageAsync(
        string walletId, OutputDescriptor clientKey, string rfqId, CancellationToken cancellationToken)
    {
        var signer = await walletProvider.GetSignerAsync(walletId, cancellationToken);
        if (signer is null)
        {
            logger?.LogWarning(
                "Wallet {WalletId} cannot sign, so this swap's preimage is random and will not "
                + "survive the loss of its record", walletId);
            return System.Security.Cryptography.RandomNumberGenerator.GetBytes(OnchainHtlc.PreimageSize);
        }

        var salt = PreimageProvisioning.IsPerArtifactDescriptor(clientKey)
            ? null
            : Convert.FromHexString(rfqId);

        return await PreimageProvisioning.DerivePreimageAsync(signer, clientKey, salt, cancellationToken);
    }

    private static OutputDescriptor UserKeyOf(ArkContract contract) => contract switch
    {
        ArkPaymentContract payment => payment.User,
        HashLockedArkPaymentContract hashLocked => hashLocked.User,
        _ => throw new InvalidOperationException(
            $"expected a payment contract to take the client key from, got {contract.GetType().Name}"),
    };
}

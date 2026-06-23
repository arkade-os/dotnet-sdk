using System.Text.Json;
using BTCPayServer.Lightning;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Models;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using KeyExtensions = NArk.Swaps.Extensions.KeyExtensions;

namespace NArk.Swaps.Boltz;

internal class BoltzSwapService(BoltzClient boltzClient, IClientTransport clientTransport, BoltzLimitsValidator limitsValidator)
{
    private static Sequence ParseSequence(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }

    // Submarine Swaps

    public async Task<SubmarineSwapResult> CreateSubmarineSwap(BOLT11PaymentRequest invoice, OutputDescriptor sender,
        CancellationToken cancellationToken = default)
    {
        var extractedSender = OutputDescriptorHelpers.Extract(sender);

        var operatorTerms = await clientTransport.GetServerInfoAsync(cancellationToken);

        var response = await boltzClient.CreateSubmarineSwapAsync(new SubmarineRequest()
        {
            Invoice = invoice.ToString(),
            RefundPublicKey =
                (extractedSender.PubKey?.ToBytes() ?? extractedSender.XOnlyPubKey.ToBytes()).ToHexStringLower(),
            From = "ARK",
            To = "BTC",
            ReferralId = boltzClient.ReferralId,
        }, cancellationToken);

        if (invoice.PaymentHash is null)
            throw new InvalidOperationException("Invoice does not contain valid payment hash");

        var hash = new uint160(Hashes.RIPEMD160(invoice.PaymentHash.ToBytes(false)), false);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender,
            receiver: KeyExtensions.ParseOutputDescriptor(response.ClaimPublicKey, operatorTerms.Network),
            hash: hash,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );


        var address = vhtlcContract.GetArkAddress();
        if (response.Address != address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet))
            throw new Exception(
                $"Address mismatch! Expected {address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet)} got {response.Address}");

        return new SubmarineSwapResult(vhtlcContract, response, address);
    }

    // Reverse Swaps

    public async Task<ReverseSwapResult> CreateReverseSwap(CreateInvoiceParams createInvoiceRequest,
        OutputDescriptor receiver,
        byte[]? preimage = null,
        ReverseSwapFeePayer feePayer = ReverseSwapFeePayer.Recipient,
        CancellationToken cancellationToken = default)
    {
        var extractedReceiver = receiver.Extract();

        // Get operator terms
        var operatorTerms = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Caller-supplied preimage enables deterministic derivation (so restored wallets can
        // re-derive and claim outstanding swaps); null falls back to random for watch-only or
        // any other no-signer scenario.
        preimage ??= RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

        var requestedAmountSats = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);

        // The fee payer decides which amount we pin: Recipient pins InvoiceAmount (invoice == requested,
        // receiver nets requested − fee); Sender pins OnchainAmount (receiver gets
        // exactly requested, invoice == requested + fee).
        var (invoiceAmount, onchainAmount) = BuildReverseAmounts(feePayer, requestedAmountSats);
        var request = new ReverseRequest
        {
            From = "BTC",
            To = "ARK",
            InvoiceAmount = invoiceAmount,
            OnchainAmount = onchainAmount,
            ClaimPublicKey =
                (extractedReceiver.PubKey?.ToBytes() ??
                                         extractedReceiver.XOnlyPubKey.ToBytes()).ToHexStringLower(), // Receiver will claim the VTXO
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            AcceptZeroConf = true,
            DescriptionHash = createInvoiceRequest.DescriptionHash?.ToString(),
            Description = createInvoiceRequest.Description,
            InvoiceExpirySeconds = Convert.ToInt32(createInvoiceRequest.Expiry.TotalSeconds),
            ReferralId = boltzClient.ReferralId,
        };

        var response = await boltzClient.CreateReverseSwapAsync(request, cancellationToken);

        if (response == null)
        {
            throw new InvalidOperationException("Failed to create reverse swap, null response from Boltz");
        }

        // Extract the sender key from Boltz's response (refundPublicKey)
        if (string.IsNullOrEmpty(response.RefundPublicKey))
        {
            throw new InvalidOperationException("Boltz did not provide refund public key");
        }

        var bolt11 = BOLT11PaymentRequest.Parse(response.Invoice, operatorTerms.Network);
        if (bolt11.PaymentHash is null || !bolt11.PaymentHash.ToBytes(false).SequenceEqual(preimageHash))
        {
            throw new InvalidOperationException("Boltz did not provide the correct preimage hash");
        }

        var invoiceAmountSats = (long)bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
        ValidateReverseAmounts(feePayer, requestedAmountSats, invoiceAmountSats, response.OnchainAmount);

        var receiverOnchainSats = ResolveExpectedOnchainAmount(feePayer, requestedAmountSats, response.OnchainAmount);
        var (feesValid, feeError) = await limitsValidator.ValidateFeesAsync(
            invoiceAmountSats, receiverOnchainSats, isReverse: true, cancellationToken);
        if (!feesValid)
            throw new InvalidOperationException(feeError);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: KeyExtensions.ParseOutputDescriptor(response.RefundPublicKey, operatorTerms.Network),
            receiver: receiver,
            preimage: preimage,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );

        // Get the claim address and validate it matches Boltz's lockup address
        var arkAddress = vhtlcContract.GetArkAddress();
        var claimAddress = arkAddress.ToString(isMainnet: operatorTerms.Network == Network.Main);

        // Validate that our computed address matches what Boltz expects
        if (claimAddress != response.LockupAddress)
        {
            throw new InvalidOperationException(
                $"Address mismatch: computed {claimAddress}, Boltz expects {response.LockupAddress}");
        }


        return new ReverseSwapResult(vhtlcContract, response, preimageHash);
    }

    // Maps the fee payer to the single Boltz amount field we pin
    internal static (long? InvoiceAmount, long? OnchainAmount) BuildReverseAmounts(
        ReverseSwapFeePayer feePayer, long requestedAmountSats) => feePayer switch
    {
        ReverseSwapFeePayer.Recipient => (requestedAmountSats, (long?)null),
        ReverseSwapFeePayer.Sender => ((long?)null, requestedAmountSats),
        _ => throw new ArgumentOutOfRangeException(nameof(feePayer), feePayer, "Unknown reverse-swap fee payer")
    };

    // Validates the amounts Boltz returned against what the fee payer requires
    internal static void ValidateReverseAmounts(
        ReverseSwapFeePayer feePayer, long requestedAmountSats, long invoiceAmountSats, long? responseOnchainSats)
    {
        switch (feePayer)
        {
            case ReverseSwapFeePayer.Recipient:
                if (invoiceAmountSats != requestedAmountSats)
                    throw new InvalidOperationException(
                        $"Invoice amount ({invoiceAmountSats} sats) does not match the requested amount ({requestedAmountSats} sats)");
                if (responseOnchainSats is not { } onchain)
                    throw new InvalidOperationException(
                        "Boltz did not return an onchain amount for a recipient-pays reverse swap");
                if (onchain <= 0 || onchain > invoiceAmountSats)
                    throw new InvalidOperationException(
                        $"Onchain amount ({onchain} sats) must be greater than zero and not exceed invoice amount ({invoiceAmountSats} sats)");
                break;

            case ReverseSwapFeePayer.Sender:
                if (invoiceAmountSats < requestedAmountSats)
                    throw new InvalidOperationException(
                        $"Invoice amount ({invoiceAmountSats} sats) must be greater than or equal to the requested amount ({requestedAmountSats} sats) to cover swap fees");
                
                if (responseOnchainSats is { } senderOnchain && senderOnchain != requestedAmountSats)
                    throw new InvalidOperationException(
                        $"Onchain amount ({senderOnchain} sats) returned by Boltz does not match the pinned requested amount ({requestedAmountSats} sats)");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(feePayer), feePayer, "Unknown reverse-swap fee payer");
        }
    }

    // The on-chain amount receiver will actually claim, stored as ArkSwap.ExpectedAmount
    internal static long ResolveExpectedOnchainAmount(
        ReverseSwapFeePayer feePayer, long requestedAmountSats, long? responseOnchainSats) => feePayer switch
    {
        ReverseSwapFeePayer.Recipient => responseOnchainSats
            ?? throw new InvalidOperationException("Boltz did not return an onchain amount for a recipient-pays reverse swap"),
        ReverseSwapFeePayer.Sender => requestedAmountSats,
        _ => throw new ArgumentOutOfRangeException(nameof(feePayer), feePayer, "Unknown reverse-swap fee payer")
    };

    // Chain Swaps

    /// <summary>
    /// Creates a BTC→ARK chain swap.
    /// Customer pays BTC on-chain → store receives Ark VTXOs.
    /// </summary>
    public async Task<ChainSwapResult> CreateBtcToArkSwapAsync(
        long amountSats,
        OutputDescriptor claimDescriptor,
        byte[]? preimage = null,
        CancellationToken ct = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(ct);
        var extractedClaim = claimDescriptor.Extract();
        var claimPubKeyHex = (extractedClaim.PubKey?.ToBytes() ?? extractedClaim.XOnlyPubKey.ToBytes())
            .ToHexStringLower();

        preimage ??= RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);
        var ephemeralKey = new Key();

        var request = new ChainRequest
        {
            From = "BTC",
            To = "ARK",
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            ClaimPublicKey = claimPubKeyHex,
            RefundPublicKey = Encoders.Hex.EncodeData(ephemeralKey.PubKey.ToBytes()),
            ServerLockAmount = amountSats,
            ReferralId = boltzClient.ReferralId,
        };

        var response = await boltzClient.CreateChainSwapAsync(request, ct);

        if (response.ClaimDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing claim details (Ark side). Raw: {SerializeChainResponse(response)}");

        if (response.LockupDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing lockup details (BTC side). Raw: {SerializeChainResponse(response)}");

        var claimDetails = response.ClaimDetails;
        var timeouts = claimDetails.TimeoutBlockHeights ?? claimDetails.Timeouts;

        // The VHTLC sender is fulmine (the Boltz sidecar wallet). Its key comes from claimDetails.serverPublicKey,
        // which is distinct from the Ark operator's signer key used as the VHTLC server.
        if (string.IsNullOrEmpty(claimDetails.ServerPublicKey))
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing serverPublicKey in ARK claim details. Raw: {SerializeChainResponse(response)}");

        var senderDescriptor = KeyExtensions.ParseOutputDescriptor(claimDetails.ServerPublicKey, operatorTerms.Network);

        if (timeouts == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing timeouts in ARK claim details. Raw: {SerializeChainResponse(response)}");

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: senderDescriptor,
            receiver: claimDescriptor,
            preimage: preimage,
            refundLocktime: new LockTime(timeouts.Refund),
            unilateralClaimDelay: ParseSequence(timeouts.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(timeouts.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(timeouts.UnilateralRefundWithoutReceiver)
        );

        // Validate address match
        var computedAddress = vhtlcContract.GetArkAddress()
            .ToString(operatorTerms.Network.ChainName == ChainName.Mainnet);
        if (computedAddress != claimDetails.LockupAddress)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: ARK address mismatch. Computed {computedAddress}, Boltz expects {claimDetails.LockupAddress}");

        return new ChainSwapResult(response, preimage, preimageHash, ephemeralKey, vhtlcContract);
    }

    /// <summary>
    /// Creates an ARK→BTC chain swap.
    /// User sends Ark VTXOs → receives BTC on-chain.
    /// </summary>
    public async Task<ChainSwapResult> CreateArkToBtcSwapAsync(
        long amountSats,
        OutputDescriptor refundDescriptor,
        byte[]? preimage = null,
        CancellationToken ct = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(ct);
        var extracted = OutputDescriptorHelpers.Extract(refundDescriptor);
        var refundPubKeyHex = Convert.ToHexString(
            extracted.PubKey?.ToBytes()
            ?? extracted.XOnlyPubKey.ToBytes()
        ).ToLowerInvariant();


        preimage ??= RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);
        var ephemeralKey = new Key();

        var request = new ChainRequest
        {
            From = "ARK",
            To = "BTC",
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            ClaimPublicKey = Encoders.Hex.EncodeData(ephemeralKey.PubKey.ToBytes()),
            RefundPublicKey = refundPubKeyHex,
            UserLockAmount = amountSats,
            ReferralId = boltzClient.ReferralId,
        };

        var response = await boltzClient.CreateChainSwapAsync(request, ct);

        if (response.LockupDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing lockup details (Ark side). Raw: {SerializeChainResponse(response)}");

        if (response.ClaimDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing claim details (BTC side). Raw: {SerializeChainResponse(response)}");

        // Reconstruct the VHTLC that Boltz/Fulmine created on the ARK side.
        // For ARK→BTC: user is the sender (refundDescriptor), Fulmine is the receiver.
        var lockupDetails = response.LockupDetails;
        var timeouts = lockupDetails.Timeouts ?? lockupDetails.TimeoutBlockHeights
            ?? throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing timeouts in ARK lockup details");

        var receiverDescriptor = KeyExtensions.ParseOutputDescriptor(
            lockupDetails.ServerPublicKey!, operatorTerms.Network);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: refundDescriptor,
            receiver: receiverDescriptor,
            preimage: preimage,
            refundLocktime: new LockTime(timeouts.Refund),
            unilateralClaimDelay: ParseSequence(timeouts.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(timeouts.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(timeouts.UnilateralRefundWithoutReceiver));

        var computedAddress = vhtlcContract.GetArkAddress()
            .ToString(operatorTerms.Network.ChainName == ChainName.Mainnet);
        if (computedAddress != lockupDetails.LockupAddress)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: ARK lockup address mismatch. " +
                $"Computed {computedAddress}, Boltz expects {lockupDetails.LockupAddress}");

        return new ChainSwapResult(response, preimage, preimageHash, ephemeralKey, vhtlcContract);
    }

    // Chain Swap Serialization

    public static string SerializeChainResponse(ChainResponse response)
    {
        return JsonSerializer.Serialize(response);
    }

    public static ChainResponse? DeserializeChainResponse(string json)
    {
        return JsonSerializer.Deserialize<ChainResponse>(json);
    }
}

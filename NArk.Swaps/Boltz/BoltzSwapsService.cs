using System.Text.Json;
using BTCPayServer.Lightning;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Swaps.Bolt12;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Bolt12;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using KeyExtensions = NArk.Swaps.Extensions.KeyExtensions;

namespace NArk.Swaps.Boltz;

internal class BoltzSwapService(BoltzClient boltzClient, IClientTransport clientTransport)
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

        return new SubmarineSwapResult(vhtlcContract, response, address, invoice.ToString());
    }

    /// <summary>
    /// Creates a submarine swap (Arkade → Lightning) where the destination is a
    /// BOLT 12 offer rather than a BOLT 11 invoice.
    /// </summary>
    /// <remarks>
    /// The method first calls Boltz's
    /// <c>POST /v2/lightning/{currency}/bolt12/fetch</c> to exchange the offer
    /// for a BOLT 12 invoice, then extracts the payment hash from that invoice
    /// and continues with the same VHTLC construction as the BOLT 11 path.
    /// The returned <see cref="SubmarineSwapResult"/> is identical in structure
    /// to the one produced by <see cref="CreateSubmarineSwap"/> — all downstream
    /// monitoring and refund logic applies without modification.
    /// </remarks>
    /// <param name="offer">A BOLT 12 offer string (<c>lno1…</c>).</param>
    /// <param name="amountSats">Amount to pay in satoshis.</param>
    /// <param name="sender">
    /// Output descriptor of the sender's key, used as the VHTLC refund key.
    /// </param>
    /// <param name="currency">
    /// Lightning currency symbol passed to Boltz, defaults to <c>"BTC"</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SubmarineSwapResult"/> with the VHTLC contract, Boltz swap
    /// details, and the Arkade lockup address.
    /// </returns>
    public async Task<SubmarineSwapResult> CreateSubmarineSwapBolt12(
        string offer,
        long amountSats,
        OutputDescriptor sender,
        string currency = "BTC",
        CancellationToken cancellationToken = default)
    {
        if (amountSats <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountSats), amountSats, "Amount must be positive.");
        if (!Bolt12InvoiceParser.IsBolt12Offer(offer))
            throw new ArgumentException(
                $"Expected a BOLT 12 offer (lno1…), got: '{offer[..Math.Min(8, offer.Length)]}…'",
                nameof(offer));

        var fetchResponse = await boltzClient.FetchBolt12InvoiceAsync(
            currency,
            new Bolt12FetchRequest { Offer = offer, Amount = amountSats },
            cancellationToken);

        Bolt12InvoiceParser.VerifyInvoiceMatchesOffer(fetchResponse.Invoice, offer);

        var paymentHashBytes = Bolt12InvoiceParser.ExtractPaymentHash(fetchResponse.Invoice);
        var hash = new uint160(Hashes.RIPEMD160(paymentHashBytes), false);

        var extractedSender = OutputDescriptorHelpers.Extract(sender);
        var operatorTerms = await clientTransport.GetServerInfoAsync(cancellationToken);

        var response = await boltzClient.CreateSubmarineSwapAsync(new SubmarineRequest
        {
            Invoice = fetchResponse.Invoice,
            RefundPublicKey =
                (extractedSender.PubKey?.ToBytes() ?? extractedSender.XOnlyPubKey.ToBytes()).ToHexStringLower(),
            From = "ARK",
            To = "BTC",
            ReferralId = boltzClient.ReferralId,
        }, cancellationToken);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender,
            receiver: KeyExtensions.ParseOutputDescriptor(response.ClaimPublicKey, operatorTerms.Network),
            hash: hash,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(
                response.TimeoutBlockHeights.UnilateralRefundWithoutReceiver)
        );

        var address = vhtlcContract.GetArkAddress();
        if (response.Address != address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet))
            throw new InvalidOperationException(
                $"Address mismatch — expected {address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet)}, " +
                $"Boltz returned {response.Address}");

        var paymentHashHex = Convert.ToHexString(paymentHashBytes).ToLowerInvariant();
        return new SubmarineSwapResult(vhtlcContract, response, address, fetchResponse.Invoice, paymentHashHex);
    }

    // Reverse Swaps

    public async Task<ReverseSwapResult> CreateReverseSwap(CreateInvoiceParams createInvoiceRequest,
        OutputDescriptor receiver,
        CancellationToken cancellationToken = default)
    {
        var extractedReceiver = receiver.Extract();

        // Get operator terms
        var operatorTerms = await clientTransport.GetServerInfoAsync(cancellationToken);

        //TODO: deterministic hash somehow instead?
        // Generate preimage and compute preimage hash using SHA256 for Boltz
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

        // First make the Boltz request to get the swap details including timeout block heights
        // Use OnchainAmount so the merchant receives the full requested amount (user pays swap fees)
        var request = new ReverseRequest
        {
            From = "BTC",
            To = "ARK",
            OnchainAmount = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi),
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

        // Verify the invoice amount is greater than onchain amount (includes fees)
        var invoiceAmountSats = bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
        var onchainAmountSats = createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        if (invoiceAmountSats < onchainAmountSats)
        {
            throw new InvalidOperationException(
                $"Invoice amount ({invoiceAmountSats} sats) must be greater than onchain amount ({onchainAmountSats} sats) to cover swap fees");
        }

        var swapFee = invoiceAmountSats - onchainAmountSats;

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

    // Chain Swaps

    /// <summary>
    /// Creates a BTC→ARK chain swap.
    /// Customer pays BTC on-chain → store receives Ark VTXOs.
    /// </summary>
    public async Task<ChainSwapResult> CreateBtcToArkSwapAsync(
        long amountSats,
        OutputDescriptor claimDescriptor,
        CancellationToken ct = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(ct);
        var extractedClaim = claimDescriptor.Extract();
        var claimPubKeyHex = (extractedClaim.PubKey?.ToBytes() ?? extractedClaim.XOnlyPubKey.ToBytes())
            .ToHexStringLower();

        var preimage = RandomUtils.GetBytes(32);
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
        string refundPubKeyHex,
        CancellationToken ct = default)
    {
        await clientTransport.GetServerInfoAsync(ct);

        var preimage = RandomUtils.GetBytes(32);
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

        return new ChainSwapResult(response, preimage, preimageHash, ephemeralKey);
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

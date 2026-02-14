using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using KeyExtensions = NArk.Swaps.Extensions.KeyExtensions;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Creates chain swaps (BTC ↔ ARK) via Boltz.
/// For BTC→ARK: constructs a VHTLCContract if Boltz provides the parameters, otherwise
/// the ARK side is handled by Boltz's fulmine sidecar and claimed via the API.
/// For ARK→BTC: the BTC Taproot HTLC is reconstructed at claim time from the stored response.
/// </summary>
internal class BoltzChainSwapService(BoltzClient boltzClient, IClientTransport clientTransport)
{
    private static Sequence ParseSequence(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }

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
            ServerLockAmount = amountSats
        };

        var response = await boltzClient.CreateChainSwapAsync(request, ct);

        Console.WriteLine($"[BoltzChainSwap] {response.Id}: raw response = {SerializeResponse(response)}");

        if (response.ClaimDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing claim details (Ark side). Raw: {SerializeResponse(response)}");

        if (response.LockupDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing lockup details (BTC side). Raw: {SerializeResponse(response)}");

        // Try to construct VHTLC contract if Boltz provides the full parameters.
        // Some Boltz versions (with fulmine sidecar) return serverPublicKey=null — in that case
        // the ARK VHTLC is managed by fulmine and claimed via the Boltz API.
        VHTLCContract? vhtlcContract = null;
        var claimDetails = response.ClaimDetails;
        Console.WriteLine($"[BoltzChainSwap] {response.Id}: claimDetails.ServerPublicKey='{claimDetails.ServerPublicKey}', timeoutBlockHeights={claimDetails.TimeoutBlockHeights != null}");
        if (!string.IsNullOrEmpty(claimDetails.ServerPublicKey) && claimDetails.TimeoutBlockHeights is { } timeouts)
        {
            vhtlcContract = new VHTLCContract(
                server: operatorTerms.SignerKey,
                sender: KeyExtensions.ParseOutputDescriptor(claimDetails.ServerPublicKey, operatorTerms.Network),
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
        }

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
            UserLockAmount = amountSats
        };

        var response = await boltzClient.CreateChainSwapAsync(request, ct);

        if (response.LockupDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing lockup details (Ark side). Raw: {SerializeResponse(response)}");

        if (response.ClaimDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing claim details (BTC side). Raw: {SerializeResponse(response)}");

        return new ChainSwapResult(response, preimage, preimageHash, ephemeralKey);
    }

    public static string SerializeResponse(ChainResponse response)
    {
        return JsonSerializer.Serialize(response);
    }

    public static ChainResponse? DeserializeResponse(string json)
    {
        return JsonSerializer.Deserialize<ChainResponse>(json);
    }
}

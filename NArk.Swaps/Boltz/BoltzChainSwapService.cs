using System.Text.Json;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Creates chain swaps (BTC ↔ ARK) via Boltz.
/// Boltz's fulmine sidecar handles the Ark VHTLC — we only need the lockup addresses.
/// The BTC Taproot HTLC (TaprootSpendInfo) is reconstructed at claim time, not at creation.
/// </summary>
internal class BoltzChainSwapService(BoltzClient boltzClient, IClientTransport clientTransport)
{
    /// <summary>
    /// Creates a BTC→ARK chain swap.
    /// Customer pays BTC on-chain → store receives Ark VTXOs.
    /// </summary>
    public async Task<ChainSwapResult> CreateBtcToArkSwapAsync(
        long amountSats,
        string claimPubKeyHex,
        CancellationToken ct = default)
    {
        await clientTransport.GetServerInfoAsync(ct); // validate connectivity

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

        if (response.ClaimDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing claim details (Ark side). Raw: {SerializeResponse(response)}");

        if (response.LockupDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing lockup details (BTC side). Raw: {SerializeResponse(response)}");

        return new ChainSwapResult(response, preimage, preimageHash, ephemeralKey);
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
        await clientTransport.GetServerInfoAsync(ct); // validate connectivity

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

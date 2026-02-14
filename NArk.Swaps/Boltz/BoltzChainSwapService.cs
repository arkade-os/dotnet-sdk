using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Creates chain swaps (BTC ↔ ARK) via Boltz.
/// Boltz's fulmine sidecar handles the Ark VHTLC — we only construct the BTC Taproot HTLC.
/// </summary>
internal class BoltzChainSwapService(BoltzClient boltzClient, IClientTransport clientTransport)
{
    /// <summary>
    /// Creates a BTC→ARK chain swap.
    /// Customer pays BTC on-chain → store receives Ark VTXOs.
    /// </summary>
    /// <param name="amountSats">Amount in satoshis to receive on Ark side.</param>
    /// <param name="claimPubKeyHex">Hex-encoded public key for the Ark claim side.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chain swap result with BTC lockup address and keys.</returns>
    public async Task<ChainSwapResult> CreateBtcToArkSwapAsync(
        long amountSats,
        string claimPubKeyHex,
        CancellationToken ct = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(ct);

        // Generate preimage + SHA256 hash (Boltz uses SHA256 for preimageHash)
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

        // Ephemeral BTC key for refund (MuSig2 on BTC side)
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

        // Validate we got the expected details
        if (response.ClaimDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing claim details (Ark side). Raw: {SerializeResponse(response)}");

        if (response.LockupDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing lockup details (BTC side). Raw: {SerializeResponse(response)}");

        // Reconstruct BTC HTLC from lockupDetails (user sends BTC here)
        TaprootSpendInfo? btcSpendInfo = null;
        var lockupDetails = response.LockupDetails;
        if (lockupDetails.SwapTree != null && lockupDetails.ServerPublicKey != null)
        {
            var boltzBtcPubKey = ECPubKey.Create(Convert.FromHexString(lockupDetails.ServerPublicKey));
            var userBtcPubKey = ECPrivKey.Create(ephemeralKey.ToBytes()).CreatePubKey();

            btcSpendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                lockupDetails.SwapTree, userBtcPubKey, boltzBtcPubKey);

            if (!BtcHtlcScripts.ValidateAddress(btcSpendInfo, lockupDetails.LockupAddress, operatorTerms.Network))
            {
                throw new InvalidOperationException(
                    $"BTC address mismatch for chain swap {response.Id}");
            }
        }

        return new ChainSwapResult(response, preimage, preimageHash, ephemeralKey, btcSpendInfo);
    }

    /// <summary>
    /// Creates an ARK→BTC chain swap.
    /// User sends Ark VTXOs → receives BTC on-chain.
    /// </summary>
    /// <param name="amountSats">Amount in satoshis to send from Ark side.</param>
    /// <param name="refundPubKeyHex">Hex-encoded public key for the Ark refund side.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chain swap result with Ark lockup address and keys.</returns>
    public async Task<ChainSwapResult> CreateArkToBtcSwapAsync(
        long amountSats,
        string refundPubKeyHex,
        CancellationToken ct = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(ct);

        // Generate preimage + SHA256 hash
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

        // Ephemeral BTC key for claiming (MuSig2 on BTC side)
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

        // Validate we got the expected details
        if (response.LockupDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing lockup details (Ark side). Raw: {SerializeResponse(response)}");

        if (response.ClaimDetails == null)
            throw new InvalidOperationException(
                $"Chain swap {response.Id}: missing claim details (BTC side). Raw: {SerializeResponse(response)}");

        // Reconstruct BTC HTLC from claimDetails (Boltz locks BTC here for us to claim)
        TaprootSpendInfo? btcSpendInfo = null;
        var claimDetails = response.ClaimDetails;
        if (claimDetails.SwapTree != null && claimDetails.ServerPublicKey != null)
        {
            var boltzBtcPubKey = ECPubKey.Create(Convert.FromHexString(claimDetails.ServerPublicKey));
            var userBtcPubKey = ECPrivKey.Create(ephemeralKey.ToBytes()).CreatePubKey();

            btcSpendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                claimDetails.SwapTree, userBtcPubKey, boltzBtcPubKey);

            if (!BtcHtlcScripts.ValidateAddress(btcSpendInfo, claimDetails.LockupAddress, operatorTerms.Network))
            {
                throw new InvalidOperationException(
                    $"BTC address mismatch for chain swap {response.Id}");
            }
        }

        return new ChainSwapResult(response, preimage, preimageHash, ephemeralKey, btcSpendInfo);
    }

    /// <summary>
    /// Serializes a ChainResponse to JSON for storage/recovery.
    /// </summary>
    public static string SerializeResponse(ChainResponse response)
    {
        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Deserializes a ChainResponse from stored JSON.
    /// </summary>
    public static ChainResponse? DeserializeResponse(string json)
    {
        return JsonSerializer.Deserialize<ChainResponse>(json);
    }
}

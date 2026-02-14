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
using NBitcoin.Secp256k1;
using KeyExtensions = NArk.Swaps.Extensions.KeyExtensions;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Creates chain swaps (BTC ↔ ARK) via Boltz, constructing the Ark VHTLC
/// and BTC Taproot HTLC, and validating addresses match.
/// </summary>
internal class BoltzChainSwapService(BoltzClient boltzClient, IClientTransport clientTransport)
{
    /// <summary>
    /// Creates a BTC→ARK chain swap.
    /// Customer pays BTC on-chain → store receives Ark VTXOs.
    /// </summary>
    /// <param name="amountSats">Amount in satoshis to receive on Ark side.</param>
    /// <param name="arkClaimDescriptor">The Ark-side descriptor that will claim the VHTLC (receiver).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chain swap result with contract, BTC lockup address, and keys.</returns>
    public async Task<ChainSwapResult> CreateBtcToArkSwapAsync(
        long amountSats,
        OutputDescriptor arkClaimDescriptor,
        CancellationToken ct = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(ct);

        // Generate preimage + SHA256 hash (Boltz uses SHA256 for preimageHash)
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

        // Ephemeral BTC key for refund (MuSig2 on BTC side)
        var ephemeralKey = new Key();

        var extractedClaim = arkClaimDescriptor.Extract();
        var claimPubKeyHex = (extractedClaim.PubKey?.ToBytes() ?? extractedClaim.XOnlyPubKey.ToBytes())
            .ToHexStringLower();

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

        // Construct Ark VHTLC from claimDetails (Boltz locks this for us)
        var claimDetails = response.ClaimDetails
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing claim details (Ark side)");

        // The VHTLC uses Hash160 = RIPEMD160(SHA256(preimage))
        var hash160 = new uint160(Hashes.RIPEMD160(preimageHash), false);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: KeyExtensions.ParseOutputDescriptor(claimDetails.ServerPublicKey!, operatorTerms.Network),
            receiver: arkClaimDescriptor,
            preimage: preimage,
            refundLocktime: new LockTime(claimDetails.TimeoutBlockHeight),
            // Chain swaps use default timeouts — Boltz doesn't specify unilateral delays for chain swaps
            // Use reasonable defaults matching the operator terms
            unilateralClaimDelay: operatorTerms.UnilateralExit,
            unilateralRefundDelay: operatorTerms.UnilateralExit,
            unilateralRefundWithoutReceiverDelay: operatorTerms.UnilateralExit
        );

        // Validate Ark address matches
        var arkAddress = vhtlcContract.GetArkAddress();
        var computedAddress = arkAddress.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet);
        if (computedAddress != claimDetails.LockupAddress)
        {
            throw new InvalidOperationException(
                $"Ark address mismatch: computed {computedAddress}, Boltz expects {claimDetails.LockupAddress}");
        }

        // Reconstruct BTC HTLC from lockupDetails (user sends BTC here)
        var lockupDetails = response.LockupDetails
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing lockup details (BTC side)");

        TaprootSpendInfo? btcSpendInfo = null;
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

        return new ChainSwapResult(vhtlcContract, response, preimage, preimageHash, ephemeralKey, btcSpendInfo);
    }

    /// <summary>
    /// Creates an ARK→BTC chain swap.
    /// User sends Ark VTXOs → receives BTC on-chain.
    /// </summary>
    /// <param name="amountSats">Amount in satoshis to send from Ark side.</param>
    /// <param name="arkRefundDescriptor">The Ark-side descriptor for refund (sender).</param>
    /// <param name="btcDestination">BTC destination address for receiving funds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chain swap result with contract, Ark lockup address, and keys.</returns>
    public async Task<ChainSwapResult> CreateArkToBtcSwapAsync(
        long amountSats,
        OutputDescriptor arkRefundDescriptor,
        BitcoinAddress btcDestination,
        CancellationToken ct = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(ct);

        // Generate preimage + SHA256 hash
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

        // Ephemeral BTC key for claiming (MuSig2 on BTC side)
        var ephemeralKey = new Key();

        var extractedRefund = arkRefundDescriptor.Extract();
        var refundPubKeyHex = (extractedRefund.PubKey?.ToBytes() ?? extractedRefund.XOnlyPubKey.ToBytes())
            .ToHexStringLower();

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

        // Construct Ark VHTLC from lockupDetails (we lock our Ark here)
        var lockupDetails = response.LockupDetails
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing lockup details (Ark side)");

        var hash160 = new uint160(Hashes.RIPEMD160(preimageHash), false);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: arkRefundDescriptor,
            receiver: KeyExtensions.ParseOutputDescriptor(lockupDetails.ServerPublicKey!, operatorTerms.Network),
            hash: hash160,
            refundLocktime: new LockTime(lockupDetails.TimeoutBlockHeight),
            unilateralClaimDelay: operatorTerms.UnilateralExit,
            unilateralRefundDelay: operatorTerms.UnilateralExit,
            unilateralRefundWithoutReceiverDelay: operatorTerms.UnilateralExit
        );

        // Validate Ark address matches
        var arkAddress = vhtlcContract.GetArkAddress();
        var computedAddress = arkAddress.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet);
        if (computedAddress != lockupDetails.LockupAddress)
        {
            throw new InvalidOperationException(
                $"Ark address mismatch: computed {computedAddress}, Boltz expects {lockupDetails.LockupAddress}");
        }

        // Reconstruct BTC HTLC from claimDetails (Boltz locks BTC here for us to claim)
        var claimDetails = response.ClaimDetails
            ?? throw new InvalidOperationException($"Chain swap {response.Id}: missing claim details (BTC side)");

        TaprootSpendInfo? btcSpendInfo = null;
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

        return new ChainSwapResult(vhtlcContract, response, preimage, preimageHash, ephemeralKey, btcSpendInfo);
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

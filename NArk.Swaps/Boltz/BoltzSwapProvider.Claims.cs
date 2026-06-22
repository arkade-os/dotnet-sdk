using Microsoft.Extensions.Logging;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Swaps.Boltz;

public partial class BoltzSwapProvider
{
     internal async Task TryClaimBtcForChainSwap(ArkSwap swap, CancellationToken cancellationToken)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToBtc)
            return;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);
        var preimageHex = swap.Get(SwapMetadata.Preimage);
        var btcAddress = swap.Get(SwapMetadata.BtcAddress);

        if (string.IsNullOrEmpty(ephemeralKeyHex) ||
            string.IsNullOrEmpty(boltzResponseJson) ||
            string.IsNullOrEmpty(preimageHex) ||
            string.IsNullOrEmpty(btcAddress))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain swap metadata for BTC claim", swap.SwapId);
            return;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            if (response == null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to deserialize Boltz response", swap.SwapId);
                return;
            }

            var claimDetails = response.ClaimDetails;
            if (claimDetails?.SwapTree == null || claimDetails.ServerPublicKey == null)
            {
                _logger?.LogWarning("Swap {SwapId}: no BTC claim details (swapTree or serverPublicKey is null)", swap.SwapId);
                return;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(claimDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                claimDetails.SwapTree, userPubKey, boltzPubKey,
                claimDetails.LockupAddress, serverInfo.Network);
            var btcDest = BitcoinAddress.Create(btcAddress, serverInfo.Network);

            // Get the lockup transaction from Boltz's status response
            var swapStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, cancellationToken);
            if (swapStatus?.Transaction?.Hex == null)
            {
                _logger?.LogDebug("Swap {SwapId}: lockup tx hex not yet available", swap.SwapId);
                return;
            }

            // Parse the lockup tx and find the output matching the HTLC address
            var lockupTx = Transaction.Parse(swapStatus.Transaction.Hex, serverInfo.Network);
            var lockupScript = BitcoinAddress.Create(claimDetails.LockupAddress, serverInfo.Network).ScriptPubKey;
            var vout = -1;
            for (var i = 0; i < lockupTx.Outputs.Count; i++)
            {
                if (lockupTx.Outputs[i].ScriptPubKey == lockupScript)
                {
                    vout = i;
                    break;
                }
            }

            if (vout < 0)
            {
                _logger?.LogWarning("Swap {SwapId}: no output matching HTLC address {Address}", swap.SwapId, claimDetails.LockupAddress);
                return;
            }

            var outpoint = new OutPoint(lockupTx.GetHash(), vout);
            var prevOut = lockupTx.Outputs[vout];

            var unsignedClaimTx = BtcTransactionBuilder.BuildKeyPathClaimTx(outpoint, prevOut, btcDest,
                await EstimateClaimRefundFeeAsync(cancellationToken));

            Transaction signedTx;
            try
            {
                _logger?.LogInformation("Swap {SwapId}: attempting MuSig2 cooperative BTC claim", swap.SwapId);
                signedTx = await _chainSwapMusig.CooperativeClaimAsync(
                    swap.SwapId, preimageHex, unsignedClaimTx, prevOut, 0,
                    ecPrivKey, boltzPubKey, spendInfo, cancellationToken);
            }
            catch (Exception coopEx)
            {
                _logger?.LogWarning(coopEx, "Swap {SwapId}: MuSig2 cooperative claim failed, falling back to script-path", swap.SwapId);

                // Fallback: script-path claim with preimage
                var claimLeaf = BtcHtlcScripts.GetClaimLeaf(claimDetails.SwapTree);
                var preimageBytes = Convert.FromHexString(preimageHex);
                BtcTransactionBuilder.SignScriptPathClaim(
                    unsignedClaimTx, 0, prevOut, spendInfo, claimLeaf,
                    preimageBytes, ephemeralKey);
                signedTx = unsignedClaimTx;
            }

            // Broadcast the signed claim transaction
            var broadcastResult = await _boltzClient.BroadcastBtcTransactionAsync(
                new BroadcastRequest { Hex = signedTx.ToHex() }, cancellationToken);

            _logger?.LogInformation("Swap {SwapId}: BTC claimed! txid={TxId}", swap.SwapId, broadcastResult.Id);

            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Status = ArkSwapStatus.Settled, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Swap {SwapId}: error claiming BTC", swap.SwapId);
        }
    }

    // ─── Cross-Signing (BTC→ARK) ──────────────────────────────────

    internal async Task TrySignBoltzBtcClaim(ArkSwap swap, CancellationToken cancellationToken)
    {
        if (swap.SwapType != ArkSwapType.ChainBtcToArk)
            return;

        // Only cross-sign once — avoid sending duplicate signatures on repeated polls
        if (swap.Get(SwapMetadata.CrossSigned) == "true")
            return;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);

        if (string.IsNullOrEmpty(ephemeralKeyHex) || string.IsNullOrEmpty(boltzResponseJson))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain swap metadata for cooperative BTC claim signing", swap.SwapId);
            return;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            if (response == null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to deserialize Boltz response for cross-signing", swap.SwapId);
                return;
            }

            var lockupDetails = response.LockupDetails;
            if (lockupDetails?.SwapTree == null || lockupDetails.ServerPublicKey == null)
            {
                _logger?.LogWarning("Swap {SwapId}: no BTC lockup details (swapTree or serverPublicKey is null)", swap.SwapId);
                return;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(lockupDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                lockupDetails.SwapTree, userPubKey, boltzPubKey,
                lockupDetails.LockupAddress, serverInfo.Network);

            _logger?.LogInformation("Swap {SwapId}: providing cooperative MuSig2 cross-signature for Boltz BTC claim", swap.SwapId);
            await _chainSwapMusig.CrossSignBoltzClaimAsync(
                swap.SwapId, ecPrivKey, boltzPubKey, spendInfo, cancellationToken);

            _logger?.LogInformation("Swap {SwapId}: cooperative cross-signature sent successfully", swap.SwapId);

            // Mark as cross-signed to avoid sending duplicate signatures
            var metadata = new Dictionary<string, string>(swap.Metadata ?? [])
            {
                [SwapMetadata.CrossSigned] = "true"
            };
            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Metadata = metadata, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-critical: Boltz can still claim via script-path with the preimage
            _logger?.LogWarning(ex, "Swap {SwapId}: cooperative cross-signing failed (non-critical, Boltz will use script-path)", swap.SwapId);
        }
    }
}
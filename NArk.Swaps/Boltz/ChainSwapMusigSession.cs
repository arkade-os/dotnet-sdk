using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NBitcoin;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Manages the MuSig2 cooperative claim/refund protocol for BTC chain swaps with Boltz.
///
/// Cooperative claim flow:
/// 1. Build unsigned claim tx
/// 2. GET /chain/{id}/claim → Boltz's pubNonce + publicKey + transactionHash (their claim tx hash)
/// 3. Generate our nonce, create MusigContext
/// 4. Cross-sign Boltz's transactionHash (so they can claim our lockup)
/// 5. POST /chain/{id}/claim with { preimage, signature (our partial sig), toSign (our unsigned tx) }
/// 6. Receive Boltz's partial signature → aggregate into final Schnorr sig → finalize key-path spend
/// </summary>
public class ChainSwapMusigSession(BoltzClient boltzClient)
{
    /// <summary>
    /// Performs a cooperative MuSig2 claim of BTC funds locked in a chain swap HTLC.
    /// </summary>
    /// <param name="swapId">The Boltz swap ID.</param>
    /// <param name="preimage">The preimage (hex) proving payment.</param>
    /// <param name="unsignedTx">Our unsigned claim transaction.</param>
    /// <param name="prevOutput">The HTLC output being spent.</param>
    /// <param name="inputIndex">Index of the input to sign (usually 0).</param>
    /// <param name="userEcPrivKey">Our ephemeral EC private key for this swap.</param>
    /// <param name="boltzPubKey">Boltz's public key from the swap response.</param>
    /// <param name="spendInfo">TaprootSpendInfo for the HTLC (needed for Merkle root tweak).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully signed transaction ready for broadcast.</returns>
    public async Task<Transaction> CooperativeClaimAsync(
        string swapId,
        string preimage,
        Transaction unsignedTx,
        TxOut prevOutput,
        int inputIndex,
        ECPrivKey userEcPrivKey,
        ECPubKey boltzPubKey,
        TaprootSpendInfo spendInfo,
        CancellationToken ct = default)
    {
        var userPubKey = userEcPrivKey.CreatePubKey();
        // Boltz sorts keys (BIP327 KeySort) before MuSig2 aggregation
        var cosignerKeys = new[] { userPubKey, boltzPubKey };

        // Step 1: Get Boltz's signing details (their nonce + tx hash they want us to cross-sign)
        var claimDetails = await boltzClient.GetChainClaimDetailsAsync(swapId, ct)
            ?? throw new InvalidOperationException($"Chain swap {swapId}: claim details not available");

        var boltzNonce = new MusigPubNonce(Convert.FromHexString(claimDetails.PubNonce));
        var boltzTxHash = Convert.FromHexString(claimDetails.TransactionHash);

        // Step 2: Cross-sign Boltz's transaction hash (so they can claim our lockup)
        var crossSignCtx = new MusigContext(cosignerKeys, sort: true, boltzTxHash, userPubKey);
        ApplyTaprootTweak(crossSignCtx, spendInfo);

        var crossSignNonce = crossSignCtx.GenerateNonce(userEcPrivKey);
        crossSignCtx.ProcessNonces([crossSignNonce.CreatePubNonce(), boltzNonce]);
        var crossPartialSig = crossSignCtx.Sign(userEcPrivKey, crossSignNonce);

        // Step 3: Compute sighash for OUR claim transaction
        var ourSighash = BtcTransactionBuilder.ComputeKeyPathSighash(
            unsignedTx, inputIndex, [prevOutput]);

        // Step 4: Create our signing context and nonce
        var ourCtx = new MusigContext(cosignerKeys, sort: true, ourSighash.ToBytes(), userPubKey);
        ApplyTaprootTweak(ourCtx, spendInfo);

        var ourNonce = ourCtx.GenerateNonce(userEcPrivKey);

        // Step 5: Submit to Boltz: preimage + cross-signature + our unsigned tx for co-signing
        var claimRequest = new ChainClaimRequest
        {
            Preimage = preimage,
            Signature = new PartialSignatureData
            {
                PubNonce = Convert.ToHexString(crossSignNonce.CreatePubNonce().ToBytes()).ToLowerInvariant(),
                PartialSignature = Convert.ToHexString(crossPartialSig.ToBytes()).ToLowerInvariant()
            },
            ToSign = new ToSignData
            {
                PubNonce = Convert.ToHexString(ourNonce.CreatePubNonce().ToBytes()).ToLowerInvariant(),
                Transaction = unsignedTx.ToHex(),
                Index = inputIndex
            }
        };

        var boltzPartialSig = await boltzClient.PostChainClaimAsync(swapId, claimRequest, ct)
            ?? throw new InvalidOperationException($"Chain swap {swapId}: Boltz did not return a partial signature");

        // Step 6: Aggregate Boltz's partial signature with ours
        var boltzResponseNonce = new MusigPubNonce(Convert.FromHexString(boltzPartialSig.PubNonce));
        var boltzResponseSig = new MusigPartialSignature(Convert.FromHexString(boltzPartialSig.PartialSignature));

        ourCtx.ProcessNonces([ourNonce.CreatePubNonce(), boltzResponseNonce]);
        var ourPartialSig = ourCtx.Sign(userEcPrivKey, ourNonce);

        // Combine into final Schnorr signature and set key-path witness
        var finalSig = ourCtx.AggregateSignatures([ourPartialSig, boltzResponseSig]);
        unsignedTx.Inputs[inputIndex].WitScript = new WitScript(new[] { finalSig.ToBytes() }, true);

        return unsignedTx;
    }

    /// <summary>
    /// Performs a cooperative MuSig2 refund of BTC funds locked in a chain swap HTLC.
    /// Used when the swap has expired and we want to reclaim our lockup.
    /// </summary>
    public async Task<Transaction> CooperativeRefundAsync(
        string swapId,
        Transaction unsignedTx,
        TxOut prevOutput,
        int inputIndex,
        ECPrivKey userEcPrivKey,
        ECPubKey boltzPubKey,
        TaprootSpendInfo spendInfo,
        CancellationToken ct = default)
    {
        var userPubKey = userEcPrivKey.CreatePubKey();
        // Boltz sorts keys (BIP327 KeySort) before MuSig2 aggregation
        var cosignerKeys = new[] { userPubKey, boltzPubKey };

        // Compute sighash for our refund transaction
        var sighash = BtcTransactionBuilder.ComputeKeyPathSighash(
            unsignedTx, inputIndex, [prevOutput]);

        // Create signing context with Taproot tweak
        var ctx = new MusigContext(cosignerKeys, sort: true, sighash.ToBytes(), userPubKey);
        ApplyTaprootTweak(ctx, spendInfo);

        var ourNonce = ctx.GenerateNonce(userEcPrivKey);

        // Send our unsigned tx to Boltz for cooperative refund
        var refundRequest = new ChainRefundRequest
        {
            PubNonce = Convert.ToHexString(ourNonce.CreatePubNonce().ToBytes()).ToLowerInvariant(),
            Transaction = unsignedTx.ToHex(),
            Index = inputIndex
        };

        var boltzResponse = await boltzClient.RefundChainSwapAsync(swapId, refundRequest, ct);

        // Aggregate signatures
        var boltzNonce = new MusigPubNonce(Convert.FromHexString(boltzResponse.PubNonce));
        var boltzSig = new MusigPartialSignature(Convert.FromHexString(boltzResponse.PartialSignature));

        ctx.ProcessNonces([ourNonce.CreatePubNonce(), boltzNonce]);
        var ourPartialSig = ctx.Sign(userEcPrivKey, ourNonce);

        var finalSig = ctx.AggregateSignatures([ourPartialSig, boltzSig]);
        unsignedTx.Inputs[inputIndex].WitScript = new WitScript(new[] { finalSig.ToBytes() }, true);

        return unsignedTx;
    }

    /// <summary>
    /// Applies the Taproot tweak to a MuSig2 context using the spend info's Merkle root.
    /// The tweak is: SHA256("TapTweak" || aggregatePubKey || merkleRoot)
    /// </summary>
    private static void ApplyTaprootTweak(MusigContext ctx, TaprootSpendInfo spendInfo)
    {
        var merkleRoot = spendInfo.MerkleRoot;
        if (merkleRoot is null) return;

        // TapTweak = SHA256("TapTweak" || aggregatePubKey || merkleRoot)
        using var sha = new NBitcoin.Secp256k1.SHA256();
        sha.InitializeTagged("TapTweak");
        sha.Write(ctx.AggregatePubKey.ToXOnlyPubKey().ToBytes());
        sha.Write(merkleRoot.ToBytes());
        var taprootHash = sha.GetHash();
        ctx.Tweak(taprootHash);
    }
}

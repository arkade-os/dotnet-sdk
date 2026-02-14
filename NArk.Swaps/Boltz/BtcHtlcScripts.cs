using NArk.Abstractions.Extensions;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Reconstructs and validates BTC Taproot HTLCs from Boltz chain swap responses.
/// The BTC HTLC has:
/// - Key path: MuSig2(userKey, boltzKey) for cooperative spend
/// - Script path: claim leaf (preimage + sig) and refund leaf (timelock + sig)
/// </summary>
public static class BtcHtlcScripts
{
    /// <summary>
    /// Reconstructs the TaprootSpendInfo for a BTC-side HTLC from Boltz's swap tree response.
    /// </summary>
    /// <param name="swapTree">The swap tree from Boltz containing claim and refund leaf scripts.</param>
    /// <param name="userKey">The user's public key (ECPubKey).</param>
    /// <param name="boltzKey">Boltz's public key (ECPubKey).</param>
    /// <returns>TaprootSpendInfo with the reconstructed Taproot tree.</returns>
    public static TaprootSpendInfo ReconstructTaprootSpendInfo(
        ChainSwapTree swapTree,
        ECPubKey userKey,
        ECPubKey boltzKey)
    {
        // Build the MuSig2 aggregate internal key from user + Boltz keys
        var internalKey = ComputeAggregateKey(userKey, boltzKey);

        // Parse the claim and refund leaf scripts from hex
        var claimScript = Script.FromBytesUnsafe(Convert.FromHexString(swapTree.ClaimLeaf.Output));
        var refundScript = Script.FromBytesUnsafe(Convert.FromHexString(swapTree.RefundLeaf.Output));

        // Build TapScript leaves — Boltz always uses TapscriptV1 (0xC0 = 192)
        var claimLeaf = new TapScript(claimScript, TapLeafVersion.C0);
        var refundLeaf = new TapScript(refundScript, TapLeafVersion.C0);

        // Build the taproot tree: [claim, refund] — order matters for Merkle root
        var builder = new TapScript[] { claimLeaf, refundLeaf }.WithTree();

        // Create TaprootSpendInfo with the aggregate internal key as X-only
        var taprootInternalKey = new TaprootInternalPubKey(internalKey.ToBytes());
        return builder.Finalize(taprootInternalKey);
    }

    /// <summary>
    /// Validates that our reconstructed address matches what Boltz returned.
    /// </summary>
    public static bool ValidateAddress(
        TaprootSpendInfo spendInfo,
        string expectedAddress,
        Network network)
    {
        var outputKey = spendInfo.OutputPubKey;
        var address = outputKey.ScriptPubKey.GetDestinationAddress(network);
        return address?.ToString() == expectedAddress;
    }

    /// <summary>
    /// Gets the claim TapScript leaf from a swap tree.
    /// </summary>
    public static TapScript GetClaimLeaf(ChainSwapTree swapTree)
    {
        var script = Script.FromBytesUnsafe(Convert.FromHexString(swapTree.ClaimLeaf.Output));
        return new TapScript(script, TapLeafVersion.C0);
    }

    /// <summary>
    /// Gets the refund TapScript leaf from a swap tree.
    /// </summary>
    public static TapScript GetRefundLeaf(ChainSwapTree swapTree)
    {
        var script = Script.FromBytesUnsafe(Convert.FromHexString(swapTree.RefundLeaf.Output));
        return new TapScript(script, TapLeafVersion.C0);
    }

    /// <summary>
    /// Computes the MuSig2 aggregate X-only public key from two keys.
    /// This is the internal key for the Taproot output.
    /// Key ordering follows BIP327: lexicographic order of compressed public keys.
    /// </summary>
    public static ECXOnlyPubKey ComputeAggregateKey(ECPubKey key1, ECPubKey key2)
    {
        return ECPubKey.MusigAggregate([key1, key2]).ToXOnlyPubKey();
    }
}

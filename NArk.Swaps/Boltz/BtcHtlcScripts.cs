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
    /// <param name="expectedAddress">Optional expected address for validation.</param>
    /// <param name="network">Optional network for address validation.</param>
    /// <returns>TaprootSpendInfo with the reconstructed Taproot tree.</returns>
    public static TaprootSpendInfo ReconstructTaprootSpendInfo(
        ChainSwapTree swapTree,
        ECPubKey userKey,
        ECPubKey boltzKey,
        string? expectedAddress = null,
        Network? network = null)
    {
        // Build the MuSig2 aggregate internal key from user + Boltz keys
        var internalKey = ComputeAggregateKey(userKey, boltzKey);

        // Parse the claim and refund leaf scripts from hex
        var claimScript = Script.FromBytesUnsafe(Convert.FromHexString(swapTree.ClaimLeaf.Output));
        var refundScript = Script.FromBytesUnsafe(Convert.FromHexString(swapTree.RefundLeaf.Output));

        // Build TapScript leaves — Boltz always uses TapscriptV1 (0xC0 = 192)
        var claimLeaf = new TapScript(claimScript, TapLeafVersion.C0);
        var refundLeaf = new TapScript(refundScript, TapLeafVersion.C0);

        // Create TaprootSpendInfo with the aggregate internal key as X-only
        var taprootInternalKey = new TaprootInternalPubKey(internalKey.ToBytes());

        // Try [claim, refund] order first (standard Boltz ordering)
        var spendInfo = new TapScript[] { claimLeaf, refundLeaf }.WithTree().Finalize(taprootInternalKey);

        // If we have an expected address, validate and try alternative ordering if needed
        if (expectedAddress != null && network != null)
        {
            if (ValidateAddress(spendInfo, expectedAddress, network))
            {
                Console.WriteLine($"[BtcHtlcScripts] Address validated with [claim, refund] order");
                return spendInfo;
            }

            Console.WriteLine($"[BtcHtlcScripts] [claim, refund] order mismatch, trying [refund, claim]...");
            var altSpendInfo = new TapScript[] { refundLeaf, claimLeaf }.WithTree().Finalize(taprootInternalKey);
            if (ValidateAddress(altSpendInfo, expectedAddress, network))
            {
                Console.WriteLine($"[BtcHtlcScripts] Address validated with [refund, claim] order");
                return altSpendInfo;
            }

            // Log diagnostic info for debugging
            var addr1 = spendInfo.OutputPubKey.ScriptPubKey.GetDestinationAddress(network);
            var addr2 = altSpendInfo.OutputPubKey.ScriptPubKey.GetDestinationAddress(network);
            Console.WriteLine($"[BtcHtlcScripts] NEITHER order matches! Expected: {expectedAddress}");
            Console.WriteLine($"[BtcHtlcScripts]   [claim,refund] → {addr1}");
            Console.WriteLine($"[BtcHtlcScripts]   [refund,claim] → {addr2}");
            Console.WriteLine($"[BtcHtlcScripts]   internalKey: {Convert.ToHexString(internalKey.ToBytes()).ToLowerInvariant()}");
            Console.WriteLine($"[BtcHtlcScripts]   userKey: {Convert.ToHexString(userKey.ToBytes()).ToLowerInvariant()}");
            Console.WriteLine($"[BtcHtlcScripts]   boltzKey: {Convert.ToHexString(boltzKey.ToBytes()).ToLowerInvariant()}");
            Console.WriteLine($"[BtcHtlcScripts]   claimLeaf hash: {claimLeaf.LeafHash}");
            Console.WriteLine($"[BtcHtlcScripts]   refundLeaf hash: {refundLeaf.LeafHash}");
            Console.WriteLine($"[BtcHtlcScripts]   claimScript: {swapTree.ClaimLeaf.Output}");
            Console.WriteLine($"[BtcHtlcScripts]   refundScript: {swapTree.RefundLeaf.Output}");
        }

        return spendInfo;
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
    /// Boltz uses x-only keys for MuSig2 aggregation (even parity assumed),
    /// so we convert both keys to even-parity before aggregating.
    /// </summary>
    public static ECXOnlyPubKey ComputeAggregateKey(ECPubKey key1, ECPubKey key2)
    {
        // Convert to x-only then back to even-parity ECPubKey to match Boltz behavior.
        // Boltz internally converts all keys to x-only before MuSig2 aggregation.
        var evenKey1 = ToEvenParity(key1);
        var evenKey2 = ToEvenParity(key2);
        return ECPubKey.MusigAggregate([evenKey1, evenKey2]).ToXOnlyPubKey();
    }

    /// <summary>
    /// Converts an ECPubKey to its even-parity version (02 prefix).
    /// This matches Boltz's behavior of using x-only keys for MuSig2.
    /// </summary>
    internal static ECPubKey ToEvenParity(ECPubKey key)
    {
        // Get x-only (32 bytes), then prepend 02 to create even-parity compressed key
        var xonly = key.ToXOnlyPubKey().ToBytes();
        var compressed = new byte[33];
        compressed[0] = 0x02;
        xonly.CopyTo(compressed, 1);
        return ECPubKey.Create(compressed);
    }
}

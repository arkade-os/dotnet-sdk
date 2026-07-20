#pragma warning disable CS1591
using System.Text;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Helpers;

public static class PsbtHelpers
{
    private const byte PsbtInTapScriptSig = 0x14;
    private const byte PsbtInTapLeafScript = 0x15;
    private const string VtxoTaprootTree = "taptree";
    private const string VtxoTreeExpiry = "expiry";
    private const string Cosigner = "cosigner";
    private const string ConditionWitness = "condition";
    private const byte ArkPsbtFieldKeyType = 222;

    /// <summary>
    /// Gets all cosigner public keys from a PSBT input
    /// </summary>
    public static IReadOnlyCollection<CosignerPublicKeyData> GetArkFieldsCosigners(this PSBTInput psbtInput)
    {
        var cosignerPrefix = new[] { ArkPsbtFieldKeyType }
            .Concat(Encoding.UTF8.GetBytes(Cosigner))
            .ToArray();

        return psbtInput.Unknown.Where(pair => StartsWith(pair.Key, cosignerPrefix)).Select(pair =>
            new CosignerPublicKeyData(pair.Key[^1], ECPubKey.Create(pair.Value))).ToList();

        bool StartsWith(byte[] bytes, byte[] prefix) => bytes.Take(prefix.Length).SequenceEqual(prefix);
    }


    public static void SetTaprootScriptSpendSignature(this PSBTInput input, ECXOnlyPubKey key, uint256 leafHash,
        SecpSchnorrSignature signature, TaprootSigHash sigHash = TaprootSigHash.Default)
    {
        var (keyBytes, valueBytes) = GetTaprootScriptSpendSignature(key, leafHash, signature, sigHash);
        input.Unknown[keyBytes] = valueBytes;
    }

    private static (byte[] key, byte[] value) GetTaprootScriptSpendSignature(ECXOnlyPubKey key, uint256 leafHash,
        SecpSchnorrSignature signature, TaprootSigHash sigHash)
    {
        byte[] keyBytes = [PsbtInTapScriptSig, .. key.ToBytes(), .. leafHash.ToBytes()];
        // BIP341: a non-default sighash needs its type byte appended to the 64-byte Schnorr
        // signature (65 bytes total), or a verifier reads the witness as SIGHASH_DEFAULT.
        var valueBytes = sigHash == TaprootSigHash.Default
            ? signature.ToBytes()
            : [.. signature.ToBytes(), (byte)sigHash];
        return (keyBytes, valueBytes);
    }

    public static void SetArkFieldConditionWitness(this PSBTInput psbtInput, WitScript script) =>
        psbtInput.Unknown[new[] { ArkPsbtFieldKeyType }.Concat(Encoding.UTF8.GetBytes(ConditionWitness)).ToArray()] =
            script.ToBytes();

    public static void SetArkFieldTapTree(this PSBTInput psbtInput, TapScript[] leaves) =>
        psbtInput.Unknown[new[] { ArkPsbtFieldKeyType }.Concat(Encoding.UTF8.GetBytes(VtxoTaprootTree)).ToArray()] =
            EncodeTaprootTree(leaves);


    // Encodes taproot script leaves per PSBT spec: {depth version script_length script}* (no leaf count prefix).
    // Param: leaves — array of tapscript byte arrays.
    /// <returns>Encoded taproot tree as byte array</returns>
    private static byte[] EncodeTaprootTree(TapScript[] leaves)
    {
        return leaves.SelectMany(EncodeLeaf).ToArray();

        IEnumerable<byte> EncodeLeaf(TapScript tapScript) =>
        [
            1, // depth
            (byte) tapScript.Version,
            ..new VarInt((ulong) tapScript.Script.Length).ToBytes(),
            ..tapScript.Script.ToBytes()
        ];
    }

    private static (byte[] key, byte[] value) GetTaprootLeafScript(TaprootSpendInfo spendInfo, TapScript leafScript)
    {
        byte[] keyBytes = [PsbtInTapLeafScript, .. spendInfo.GetControlBlock(leafScript).ToBytes()];
        byte[] valueBytes = [.. leafScript.Script.ToBytes(), (byte)leafScript.Version];
        return (keyBytes, valueBytes);
    }

    public static void SetTaprootLeafScript(this PSBTInput input, TaprootSpendInfo spendInfo, TapScript leafScript)
    {
        var (keyBytes, valueBytes) = GetTaprootLeafScript(spendInfo, leafScript);
        input.Unknown[keyBytes] = valueBytes;
    }

    public static async Task SignAndFillPsbt(IArkadeWalletSigner signer, ArkCoin coin, PSBT psbt, TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        TaprootSigHash sigHash = TaprootSigHash.Default, CancellationToken cancellationToken = default)
    {
        var psbtInput = coin.FillPsbtInput(psbt);

        if (psbtInput is null || coin.SignerDescriptor is null)
            return;

        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int)psbtInput.Index, coin.SpendingScript.LeafHash)
            {
                SigHash = sigHash
            });

        var (pubKey, sig) = await signer.Sign(coin.SignerDescriptor, hash, cancellationToken);

        psbtInput.SetTaprootScriptSpendSignature(pubKey, coin.SpendingScript.LeafHash, sig, sigHash);
    }

    /// <summary>
    /// Reads the taproot leaf script (PSBT_IN_TAP_LEAF_SCRIPT, <c>0x15</c>) set by
    /// <see cref="SetTaprootLeafScript"/> back out of a PSBT input.
    /// </summary>
    /// <returns><c>true</c> when the input carries a leaf script.</returns>
    public static bool TryGetTaprootLeafScript(this PSBTInput input, out byte[] controlBlock, out Script leafScript)
    {
        foreach (var pair in input.Unknown)
        {
            if (pair.Key.Length > 1 && pair.Key[0] == PsbtInTapLeafScript && pair.Value.Length > 1)
            {
                controlBlock = pair.Key[1..];
                // value = script bytes + 1-byte leaf version
                leafScript = new Script(pair.Value[..^1]);
                return true;
            }
        }

        controlBlock = [];
        leafScript = Script.Empty;
        return false;
    }

    /// <summary>
    /// Reads all taproot script-spend signatures (PSBT_IN_TAP_SCRIPT_SIG, <c>0x14</c>)
    /// set by <see cref="SetTaprootScriptSpendSignature"/>, keyed by lowercase
    /// hex-encoded x-only public key.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> GetTaprootScriptSpendSignatures(this PSBTInput input)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in input.Unknown)
        {
            if (pair.Key.Length == 1 + 32 + 32 && pair.Key[0] == PsbtInTapScriptSig)
                result[Convert.ToHexString(pair.Key.AsSpan(1, 32)).ToLowerInvariant()] = pair.Value;
        }

        return result;
    }

    /// <summary>
    /// Reads the Arkade condition-witness field set by
    /// <see cref="SetArkFieldConditionWitness"/>, or <c>null</c> when absent.
    /// </summary>
    public static WitScript? GetArkFieldConditionWitness(this PSBTInput input)
    {
        var conditionKey = new[] { ArkPsbtFieldKeyType }
            .Concat(Encoding.UTF8.GetBytes(ConditionWitness)).ToArray();

        foreach (var pair in input.Unknown)
        {
            if (pair.Key.SequenceEqual(conditionKey))
                return new WitScript(pair.Value);
        }

        return null;
    }

}

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
        SecpSchnorrSignature signature)
    {
        var (keyBytes, valueBytes) = GetTaprootScriptSpendSignature(key, leafHash, signature);
        input.Unknown[keyBytes] = valueBytes;
    }

    private static (byte[] key, byte[] value) GetTaprootScriptSpendSignature(ECXOnlyPubKey key, uint256 leafHash,
        SecpSchnorrSignature signature)
    {
        byte[] keyBytes = [PsbtInTapScriptSig, .. key.ToBytes(), .. leafHash.ToBytes()];
        var valueBytes = signature.ToBytes();
        return (keyBytes, valueBytes);
    }

    public static void SetArkFieldConditionWitness(this PSBTInput psbtInput, WitScript script) =>
        psbtInput.Unknown[new[] { ArkPsbtFieldKeyType }.Concat(Encoding.UTF8.GetBytes(ConditionWitness)).ToArray()] =
            script.ToBytes();

    public static void SetArkFieldTapTree(this PSBTInput psbtInput, TapScript[] leaves) =>
        psbtInput.Unknown[new[] { ArkPsbtFieldKeyType }.Concat(Encoding.UTF8.GetBytes(VtxoTaprootTree)).ToArray()] =
            EncodeTaprootTree(leaves);


    /// <summary>
    /// Encodes a collection of taproot script leaves into a byte array following PSBT spec
    /// Format: {<depth> <version> <script_length> <script>}* (no leaf count prefix)
    /// </summary>
    /// <param name="leaves">Array of tapscript byte arrays</param>
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

        psbtInput.SetTaprootScriptSpendSignature(pubKey, coin.SpendingScript.LeafHash, sig);
    }



}
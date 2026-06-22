using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.Fees;

/// <summary>
/// Computes Bitcoin transaction weight units (WU) for Arkade VTXO inputs and transactions,
/// matching the weight formula used by the Arkade server (<c>baseSize×3 + totalSize</c>).
/// </summary>
/// <remarks>
/// Weight unit formula: non-witness bytes × 4 + witness bytes × 1.
/// <para>
/// Note: <see cref="NBitcoin.PSBT.TryGetVirtualSize"/> cannot be used here because
/// the codebase stores taproot script-path metadata in raw PSBT <c>Unknown</c> fields.
/// NBitcoin does not parse those back into its structured <c>TaprootLeafScripts</c>
/// collection, so it silently falls back to the much-smaller key-path estimate (69 vbytes
/// vs the correct ~102 vbytes for a standard <c>ArkPaymentContract</c> input).
/// </para>
/// </remarks>
public static class ArkTxWeightEstimator
{
    // Base segwit tx with 0 inputs and 0 outputs:
    // non-witness: version(4) + in_count(1) + out_count(1) + locktime(4) = 10 bytes × 4 = 40 WU
    // witness overhead: marker(1) + flag(1) = 2 WU
    /// <summary>Weight units for a segwit transaction with no inputs and no outputs.</summary>
    public const int BaseTxWu = 42;

    // Non-witness per input: outpoint(36) + scriptSig_len(1) + nSequence(4) = 41 bytes × 4
    private const int InputNonWitnessWu = 41 * 4;

    // Non-witness per P2TR output: value(8) + spk_len(1) + spk(34) = 43 bytes × 4
    /// <summary>Weight units for a single P2TR (taproot) output.</summary>
    public const int P2TrOutputWu = 43 * 4;

    /// <summary>
    /// Returns the weight units contributed by a single Arkade VTXO input when
    /// spent via its tapscript (script-path) spending path.
    /// </summary>
    /// <remarks>
    /// The signature count is inferred from the number of <c>OP_CHECKSIG</c> /
    /// <c>OP_CHECKSIGVERIFY</c> opcodes in the spending script, matching how the
    /// Arkade server's <c>MultisigClosure.Witness</c> builds the witness stack.
    /// For example, a standard collaborative path (<c>[user_key] OP_CHECKSIGVERIFY
    /// [server_key] OP_CHECKSIG</c>) contributes 2 signatures = 430 WU total.
    /// </remarks>
    /// <param name="coin">The VTXO to measure.</param>
    /// <returns>Weight units: non-witness bytes × 4 + witness bytes × 1.</returns>
    public static int GetInputWeightUnits(ArkCoin coin)
    {
        var spendInfo = coin.Contract.GetTaprootSpendInfo();
        var cbLen = spendInfo.GetControlBlock(coin.SpendingScript).ToBytes().Length;
        var scriptLen = coin.SpendingScript.Script.ToBytes().Length;

        // Count required signatures from script opcodes — mirrors arkd's MultisigClosure.Witness
        // which iterates over PubKeys (one sig per OP_CHECKSIG / OP_CHECKSIGVERIFY in the script).
        var sigCount = CountRequiredSignatures(coin.SpendingScript.Script);

        // witness: [item_count(1)] [sig_len(1)+sig(64)] × sigCount [script_len(1)+script] [cb_len(1)+cb]
        var witnessWu = 1 + sigCount * (1 + 64) + 1 + scriptLen + 1 + cbLen;

        // Condition items (e.g. hash-lock preimage) precede the signatures in the witness stack.
        // WitScript.ToBytes() = [count][len+item]…; exclude the count byte — it folds into our count.
        if (coin.SpendingConditionWitness is { } cw)
        {
            witnessWu += cw.ToBytes().Length - 1;
        }

        return InputNonWitnessWu + witnessWu;
    }

    /// <summary>
    /// Returns the weight units for a single transaction output, computed from its
    /// serialized byte size (non-witness only: value + script).
    /// </summary>
    /// <remarks>
    /// Use this for any output type not covered by the typed constants
    /// (<see cref="P2TrOutputWu"/>, <see cref="P2AOutputWu"/>), such as an asset-packet
    /// OP_RETURN output produced by <c>AssetPacketBuilder</c>.
    /// </remarks>
    /// <param name="output">The output to measure.</param>
    /// <returns>Weight units: <c>(8 + scriptLenVarint + scriptLen) × 4</c>.</returns>
    public static int GetOutputWeightUnits(TxOut output)
    {
        var scriptLen = output.ScriptPubKey.ToBytes().Length;
        // non-witness: value(8) + VarInt(scriptLen) + scriptLen
        return (8 + VarIntSize(scriptLen) + scriptLen) * 4;
    }

    // Counts the number of distinct signatures required by a tapscript spending path by
    // counting OP_CHECKSIG and OP_CHECKSIGVERIFY opcodes. This matches the number of
    // PubKeys iterated by arkd's MultisigClosure.Witness.
    private static int CountRequiredSignatures(Script script) =>
        script.ToOps().Count(op =>
            op.Code is OpcodeType.OP_CHECKSIG or OpcodeType.OP_CHECKSIGVERIFY);

    // Bitcoin CompactSize (VarInt) byte count for a given value.
    private static int VarIntSize(int value) => value switch
    {
        < 0xFD => 1,
        <= 0xFFFF => 3,
        _ => 5
    };
}

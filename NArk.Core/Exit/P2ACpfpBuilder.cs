using NBitcoin;

namespace NArk.Core.Exit;

/// <summary>
/// Builds v3 CPFP child transactions that spend P2A anchor outputs,
/// enabling 1p1c package relay for Ark virtual tree transactions.
/// </summary>
public static class P2ACpfpBuilder
{
    /// <summary>
    /// Standard BIP 431 P2A script (OP_1 = 0x51).
    /// </summary>
    private static readonly Script Bip431P2A = new(OpcodeType.OP_1);

    /// <summary>
    /// Ark protocol P2A marker (OP_1 PUSH2 "Ns").
    /// </summary>
    private static readonly Script ArkP2A = Script.FromHex("51024e73");

    /// <summary>
    /// Find the P2A anchor output in a transaction.
    /// Checks for both BIP 431 standard P2A and Ark protocol marker.
    /// </summary>
    public static (OutPoint Outpoint, TxOut TxOut)? FindP2AAnchor(Transaction tx)
    {
        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            var script = output.ScriptPubKey;

            if (script == Bip431P2A || script == ArkP2A)
                return (new OutPoint(tx, i), output);
        }
        return null;
    }

    /// <summary>
    /// Build a v3 CPFP child transaction that spends the P2A anchor output
    /// and provides fees for the entire 1p1c package.
    /// </summary>
    /// <param name="parent">The parent transaction containing a P2A anchor output.</param>
    /// <param name="targetFeeRate">Target fee rate for the package (parent + child combined).</param>
    /// <param name="feeUtxo">A confirmed wallet UTXO to fund the fees.</param>
    /// <param name="feeUtxoPrevOut">The TxOut being spent by feeUtxo (for signing).</param>
    /// <param name="changeScript">Script for change output.</param>
    /// <param name="signingKey">Key to sign the fee UTXO input (P2TR keypath spend).</param>
    /// <returns>The signed CPFP child transaction.</returns>
    public static Transaction BuildCpfpChild(
        Transaction parent,
        FeeRate targetFeeRate,
        OutPoint feeUtxo,
        TxOut feeUtxoPrevOut,
        Script changeScript,
        Key signingKey)
    {
        var anchor = FindP2AAnchor(parent)
            ?? throw new InvalidOperationException("Parent transaction has no P2A anchor output");

        var child = parent.Clone();
        child.Inputs.Clear();
        child.Outputs.Clear();
        child.Version = 3;

        // Input 0: P2A anchor (anyone-can-spend, empty witness)
        child.Inputs.Add(anchor.Outpoint);

        // Input 1: Fee funding UTXO
        child.Inputs.Add(feeUtxo);

        // Calculate fees: total package fee = targetFeeRate × (parent_vsize + child_vsize)
        var parentVsize = parent.GetVirtualSize();
        // Estimate child: ~10 overhead + 41 anchor input + 58 P2TR keypath input + 43 P2TR output ≈ 152 vbytes
        const int estimatedChildVsize = 155;
        var totalFee = targetFeeRate.GetFee(parentVsize + estimatedChildVsize);

        // Change = fee UTXO value + anchor value - total fee
        var totalInput = feeUtxoPrevOut.Value + anchor.TxOut.Value;
        var change = totalInput - totalFee;

        if (change > Money.Zero)
        {
            child.Outputs.Add(new TxOut(change, changeScript));
        }

        // Sign input 1 (fee UTXO) with P2TR keypath spend
        var prevOuts = new[] { anchor.TxOut, feeUtxoPrevOut };
        var precomputedData = child.PrecomputeTransactionData(prevOuts);
        var sighash = child.GetSignatureHashTaproot(
            precomputedData,
            new TaprootExecutionData(1) { SigHash = TaprootSigHash.Default });

        var sig = signingKey.SignTaprootKeySpend(sighash, TaprootSigHash.Default);
        child.Inputs[1].WitScript = new WitScript(new[] { sig.ToBytes() }, true);

        return child;
    }
}

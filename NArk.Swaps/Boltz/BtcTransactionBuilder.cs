using NBitcoin;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Builds BTC L1 transactions for chain swap claiming and refunding.
/// Supports both key-path (MuSig2 cooperative) and script-path (fallback) spends.
/// </summary>
public static class BtcTransactionBuilder
{
    /// <summary>
    /// Builds an unsigned transaction for MuSig2 cooperative key-path claim.
    /// The transaction has a single input (the HTLC output) and a single output (destination).
    /// </summary>
    /// <param name="outpoint">The HTLC output to spend.</param>
    /// <param name="prevOutput">The previous output (amount + scriptPubKey).</param>
    /// <param name="destination">The destination address for claimed funds.</param>
    /// <param name="feeSats">The fee in satoshis.</param>
    /// <returns>Unsigned transaction ready for MuSig2 signing.</returns>
    public static Transaction BuildKeyPathClaimTx(
        OutPoint outpoint,
        TxOut prevOutput,
        BitcoinAddress destination,
        long feeSats)
    {
        var tx = destination.Network.CreateTransaction();
        tx.Version = 2;

        tx.Inputs.Add(new TxIn(outpoint));

        var outputAmount = prevOutput.Value - Money.Satoshis(feeSats);
        tx.Outputs.Add(new TxOut(outputAmount, destination));

        return tx;
    }

    /// <summary>
    /// Signs and completes a script-path claim transaction using preimage.
    /// Witness: &lt;signature&gt; &lt;preimage&gt; &lt;claim_script&gt; &lt;control_block&gt;
    /// </summary>
    /// <param name="tx">The unsigned transaction to sign.</param>
    /// <param name="inputIndex">Input index to sign.</param>
    /// <param name="prevOutput">The previous output being spent.</param>
    /// <param name="spendInfo">TaprootSpendInfo for computing control block.</param>
    /// <param name="claimLeaf">The claim script leaf.</param>
    /// <param name="preimage">The preimage (32 bytes).</param>
    /// <param name="claimKey">The private key for signing the claim.</param>
    public static void SignScriptPathClaim(
        Transaction tx,
        int inputIndex,
        TxOut prevOutput,
        TaprootSpendInfo spendInfo,
        TapScript claimLeaf,
        byte[] preimage,
        Key claimKey)
    {
        // Compute the taproot sighash for script-path spend
        var precomputedData = tx.PrecomputeTransactionData([prevOutput]);
        var execData = new TaprootExecutionData(inputIndex, claimLeaf.LeafHash)
        {
            SigHash = TaprootSigHash.Default
        };
        var sighash = tx.GetSignatureHashTaproot(precomputedData, execData);

        // Sign with the claim key (script-path spend)
        var sig = claimKey.SignTaprootScriptSpend(sighash, TaprootSigHash.Default);

        // Build the witness: <signature> <preimage> <script> <controlBlock>
        var controlBlock = spendInfo.GetControlBlock(claimLeaf);
        tx.Inputs[inputIndex].WitScript = new WitScript(new[]
        {
            sig.ToBytes(),
            preimage,
            claimLeaf.Script.ToBytes(),
            controlBlock.ToBytes()
        }, true);
    }

    /// <summary>
    /// Signs and completes a script-path refund transaction after timeout.
    /// Witness: &lt;signature&gt; &lt;refund_script&gt; &lt;control_block&gt;
    /// </summary>
    /// <param name="tx">The unsigned transaction to sign.</param>
    /// <param name="inputIndex">Input index to sign.</param>
    /// <param name="prevOutput">The previous output being spent.</param>
    /// <param name="spendInfo">TaprootSpendInfo for computing control block.</param>
    /// <param name="refundLeaf">The refund script leaf.</param>
    /// <param name="timeout">The CLTV timeout block height.</param>
    /// <param name="refundKey">The private key for signing the refund.</param>
    public static void SignScriptPathRefund(
        Transaction tx,
        int inputIndex,
        TxOut prevOutput,
        TaprootSpendInfo spendInfo,
        TapScript refundLeaf,
        uint timeout,
        Key refundKey)
    {
        // Set locktime for CLTV
        tx.LockTime = new LockTime(timeout);
        // Enable nLockTime by setting sequence < 0xFFFFFFFF
        tx.Inputs[inputIndex].Sequence = new Sequence(0xFFFFFFFE);

        // Compute the taproot sighash for script-path spend
        var precomputedData = tx.PrecomputeTransactionData([prevOutput]);
        var execData = new TaprootExecutionData(inputIndex, refundLeaf.LeafHash)
        {
            SigHash = TaprootSigHash.Default
        };
        var sighash = tx.GetSignatureHashTaproot(precomputedData, execData);

        // Sign with the refund key (script-path spend)
        var sig = refundKey.SignTaprootScriptSpend(sighash, TaprootSigHash.Default);

        // Build the witness: <signature> <script> <controlBlock>
        var controlBlock = spendInfo.GetControlBlock(refundLeaf);
        tx.Inputs[inputIndex].WitScript = new WitScript(new[]
        {
            sig.ToBytes(),
            refundLeaf.Script.ToBytes(),
            controlBlock.ToBytes()
        }, true);
    }

    /// <summary>
    /// Computes the sighash for a key-path spend (used for MuSig2 cooperative signing).
    /// </summary>
    public static uint256 ComputeKeyPathSighash(
        Transaction tx,
        int inputIndex,
        TxOut[] prevOutputs,
        TaprootSigHash sigHash = TaprootSigHash.Default)
    {
        var precomputedData = tx.PrecomputeTransactionData(prevOutputs);
        var execData = new TaprootExecutionData(inputIndex)
        {
            SigHash = sigHash
        };
        return tx.GetSignatureHashTaproot(precomputedData, execData);
    }
}

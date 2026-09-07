using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>
/// Builds the Bitcoin-L1 transaction that takes an HTLC back once its refund leaf has matured.
/// </summary>
/// <remarks>
/// <para>
/// The on-board corridor's only recourse. There the client funds the L1 HTLC first, so if the solver
/// never funds the Arkade side there is nothing to claim and nothing to refund on Arkade — the sats
/// are on L1, behind this leaf, and no counterparty signature reaches them.
/// </para>
/// <para>
/// Two things separate this from <see cref="OnchainClaimBuilder"/>, and both are consensus rules
/// rather than conventions: the transaction carries the leaf's locktime in its own
/// <c>nLockTime</c>, and its inputs must be non-final for <c>OP_CHECKLOCKTIMEVERIFY</c> to be
/// evaluated at all. Miss either and the script fails on a transaction that is otherwise correct.
/// </para>
/// </remarks>
public static class OnchainRefundBuilder
{
    /// <summary>
    /// The input sequence a timelocked spend must carry.
    /// </summary>
    /// <remarks>
    /// <c>OP_CHECKLOCKTIMEVERIFY</c> is a no-op on an input whose sequence is <c>0xFFFFFFFF</c> —
    /// the script would pass with any locktime at all, and the transaction would then be rejected as
    /// non-final by the node instead. <c>0xFFFFFFFE</c> is the usual choice: non-final, and still
    /// opting out of BIP-125 replaceability.
    /// </remarks>
    public const uint TimelockedSequence = 0xFFFFFFFE;

    /// <summary>
    /// Build and sign the refund.
    /// </summary>
    /// <param name="htlc">The HTLC being refunded.</param>
    /// <param name="inputs">The outputs at its address to sweep. All of them, not the largest.</param>
    /// <param name="refundAddress">Where the sweep pays.</param>
    /// <param name="feeRate">What to pay for the weight.</param>
    /// <param name="signAsync">Produces a BIP-340 signature over a sighash.</param>
    /// <param name="cancellationToken">Cancels between signatures.</param>
    /// <returns>The signed transaction, ready to broadcast.</returns>
    /// <exception cref="InvalidOperationException">
    /// There is nothing at the HTLC, or the fee leaves nothing worth paying out.
    /// </exception>
    /// <remarks>
    /// Whether the leaf has actually matured is deliberately not checked here: maturity is measured
    /// against the chain's median time past, which this has no way to read, and inventing a wall
    /// clock comparison would produce a transaction that looks due and is refused as non-final. Ask
    /// <see cref="OnchainReceiveGates.RefundIsDue"/> with the tip's median time past first.
    /// </remarks>
    public static async Task<Transaction> BuildAsync(
        OnchainHtlc htlc,
        IReadOnlyList<BoardingUtxo> inputs,
        BitcoinAddress refundAddress,
        FeeRate feeRate,
        Func<uint256, Task<SecpSchnorrSignature>> signAsync,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("there is nothing at the HTLC to refund");
        }

        var tx = Transaction.Create(refundAddress.Network);

        // The leaf's own locktime, on the transaction. CLTV compares the stack value against this
        // field, so a refund carrying anything lower fails the script outright.
        tx.LockTime = htlc.RefundLocktime;

        var spent = new TxOut[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            tx.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(inputs[i].Txid), inputs[i].Vout))
            {
                Sequence = new Sequence(TimelockedSequence),
            });
            spent[i] = new TxOut(Money.Satoshis(inputs[i].Amount), htlc.PkScript);
        }

        var total = inputs.Aggregate(0L, (sum, u) => sum + (long)u.Amount);
        var payout = Money.Satoshis(total) - feeRate.GetFee(VirtualSize(htlc, inputs.Count, refundAddress));
        if (payout.Satoshi < OnchainClaimBuilder.DustSats)
        {
            throw new InvalidOperationException(
                $"refunding {total} sats at this fee rate leaves {payout.Satoshi}, under the "
                + $"{OnchainClaimBuilder.DustSats} dust limit — there is nothing worth broadcasting");
        }

        tx.Outputs.Add(payout, refundAddress);

        var leaf = htlc.RefundLeaf.ToTapScript(TapLeafVersion.C0);
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var execData = new TaprootExecutionData(i, leaf.LeafHash) { SigHash = TaprootSigHash.Default };
            var signature = await signAsync(tx.GetSignatureHashTaproot(spent, execData));

            // No preimage on this leaf: the signature alone satisfies it, then script and control
            // block as taproot requires.
            tx.Inputs[i].WitScript = new WitScript(
                Op.GetPushOp(signature.ToBytes()),
                Op.GetPushOp(htlc.RefundLeaf.ToBytes()),
                Op.GetPushOp(htlc.RefundControlBlock.ToBytes()));
        }

        return tx;
    }

    /// <summary>
    /// The signed transaction's virtual size, in vbytes.
    /// </summary>
    /// <remarks>
    /// Computed rather than measured, for the reason <see cref="OnchainClaimBuilder.VirtualSize"/>
    /// gives: the fee has to be known before the signatures exist. Three witness items here rather
    /// than four — the refund leaf takes no preimage.
    /// </remarks>
    /// <param name="htlc">The HTLC being refunded.</param>
    /// <param name="inputCount">How many of its outputs are being swept.</param>
    /// <param name="refundAddress">Where the sweep pays.</param>
    /// <returns>The size in vbytes.</returns>
    public static int VirtualSize(OnchainHtlc htlc, int inputCount, BitcoinAddress refundAddress)
    {
        var baseSize = 4 + 4 + 1 + 1
                       + 8 + 1 + refundAddress.ScriptPubKey.Length
                       + inputCount * (32 + 4 + 1 + 4);

        var controlBlock = htlc.RefundControlBlock.ToBytes();
        var witnessSize = 2 + inputCount * (
            1
            + 1 + 64
            + VarIntSize(htlc.RefundLeaf.Length) + htlc.RefundLeaf.Length
            + VarIntSize(controlBlock.Length) + controlBlock.Length);

        var weight = baseSize * 4 + witnessSize;
        return (weight + 3) / 4;
    }

    private static int VarIntSize(int value) => value < 0xfd ? 1 : value <= 0xffff ? 3 : 5;
}

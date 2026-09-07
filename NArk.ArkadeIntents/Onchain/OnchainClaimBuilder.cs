using System.Security.Cryptography;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>
/// Builds the Bitcoin-L1 transaction that claims an HTLC by publishing its preimage.
/// </summary>
/// <remarks>
/// Signing is a delegate rather than a wallet, so the transaction this produces can be checked
/// against a recomputed sighash without one. That check is the point: everything else about a
/// script-path spend is shaped correctly long before it is spendable, and a wrong sighash, leaf hash
/// or witness order all produce a transaction that looks right and the network rejects.
/// </remarks>
public static class OnchainClaimBuilder
{
    /// <summary>The smallest payout worth broadcasting, in sats.</summary>
    public const long DustSats = 330;

    /// <summary>
    /// Build and sign the claim.
    /// </summary>
    /// <param name="htlc">The HTLC being claimed.</param>
    /// <param name="inputs">The outputs at its address to sweep. All of them, not the largest.</param>
    /// <param name="preimage">The secret the claim leaf gates on.</param>
    /// <param name="payoutAddress">Where the sweep pays.</param>
    /// <param name="feeRate">What to pay for the weight.</param>
    /// <param name="signAsync">Produces a BIP-340 signature over a sighash.</param>
    /// <param name="cancellationToken">Cancels between signatures.</param>
    /// <returns>The signed transaction, ready to broadcast.</returns>
    /// <exception cref="ArgumentException"><paramref name="preimage"/> is not this HTLC's.</exception>
    /// <exception cref="InvalidOperationException">The fee leaves nothing worth paying out.</exception>
    public static async Task<Transaction> BuildAsync(
        OnchainHtlc htlc,
        IReadOnlyList<BoardingUtxo> inputs,
        byte[] preimage,
        BitcoinAddress payoutAddress,
        FeeRate feeRate,
        Func<uint256, Task<SecpSchnorrSignature>> signAsync,
        CancellationToken cancellationToken = default)
    {
        // Checked before anything is built, because the failure is otherwise silent until the
        // network rejects it — and a claim that cannot be spent has still leaked nothing, but a
        // caller that thinks it published the preimage will stop watching.
        if (preimage.Length != OnchainHtlc.PreimageSize
            || new uint256(System.Security.Cryptography.SHA256.HashData(preimage), lendian: false)
                != htlc.PaymentHash)
        {
            throw new ArgumentException(
                "the preimage does not hash to this HTLC's payment hash", nameof(preimage));
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("there is nothing at the HTLC to claim");
        }

        var tx = Transaction.Create(payoutAddress.Network);
        var spent = new TxOut[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            tx.Inputs.Add(new OutPoint(uint256.Parse(inputs[i].Txid), inputs[i].Vout));
            spent[i] = new TxOut(Money.Satoshis(inputs[i].Amount), htlc.PkScript);
        }

        var total = inputs.Aggregate(0L, (sum, u) => sum + (long)u.Amount);
        var payout = Money.Satoshis(total) - feeRate.GetFee(VirtualSize(htlc, inputs.Count, payoutAddress));
        if (payout.Satoshi < DustSats)
        {
            throw new InvalidOperationException(
                $"claiming {total} sats at this fee rate leaves {payout.Satoshi}, under the {DustSats} "
                + "dust limit — there is nothing worth broadcasting");
        }

        // One output, so the fee comes out of the sweep. A change address would be a second script
        // for the wallet to watch, bought for nothing: this claims a contract, not a balance.
        tx.Outputs.Add(payout, payoutAddress);

        var leaf = htlc.ClaimLeaf.ToTapScript(TapLeafVersion.C0);
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var execData = new TaprootExecutionData(i, leaf.LeafHash) { SigHash = TaprootSigHash.Default };
            var signature = await signAsync(tx.GetSignatureHashTaproot(spent, execData));

            // The leaf reads the preimage off the top of the stack before hashing it, so of the two
            // stack items it is the one pushed last. Script then control block, as taproot requires.
            tx.Inputs[i].WitScript = new WitScript(
                Op.GetPushOp(signature.ToBytes()),
                Op.GetPushOp(preimage),
                Op.GetPushOp(htlc.ClaimLeaf.ToBytes()),
                Op.GetPushOp(htlc.ClaimControlBlock.ToBytes()));
        }

        return tx;
    }

    /// <summary>
    /// The signed transaction's virtual size, in vbytes.
    /// </summary>
    /// <remarks>
    /// Computed from the parts rather than measured, because the fee has to be known before the
    /// signatures exist. Every witness item is counted at the size it will actually be: a 64-byte
    /// BIP-340 signature, a 32-byte preimage, and the leaf and control block this HTLC already
    /// carries — none of which is an estimate.
    /// </remarks>
    public static int VirtualSize(OnchainHtlc htlc, int inputCount, BitcoinAddress payoutAddress)
    {
        // version + locktime + counts, and one output.
        var baseSize = 4 + 4 + 1 + 1
                       + 8 + 1 + payoutAddress.ScriptPubKey.Length
                       + inputCount * (32 + 4 + 1 + 4);

        // Marker, flag, and one witness stack per input: four items, each length-prefixed.
        var witnessSize = 2 + inputCount * (
            1
            + 1 + 64
            + 1 + OnchainHtlc.PreimageSize
            + VarIntSize(htlc.ClaimLeaf.Length) + htlc.ClaimLeaf.Length
            + VarIntSize(htlc.ClaimControlBlock.ToBytes().Length) + htlc.ClaimControlBlock.ToBytes().Length);

        // Weight is base×4 + witness, and vsize is weight rounded up to whole units.
        var weight = baseSize * 4 + witnessSize;
        return (weight + 3) / 4;
    }

    private static int VarIntSize(int value) => value < 0xfd ? 1 : value <= 0xffff ? 3 : 5;
}

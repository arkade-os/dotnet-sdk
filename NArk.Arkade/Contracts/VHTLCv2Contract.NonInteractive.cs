using NArk.Abstractions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Contracts;

public partial class VHTLCv2Contract
{
    /// <summary>Opens a BTC-only covenant claim without a wallet signature; each input requires its own pinned payout output.</summary>
    /// <param name="walletIdentifier">The wallet tracking this lockup; no signer is required.</param>
    /// <param name="vtxo">An unspent, unswept output of this contract.</param>
    /// <param name="preimage">The 32-byte secret committed by the contract. Submission reveals it.</param>
    /// <returns>A signerless coin carrying the committed claim leaf and emulator covenant.</returns>
    /// <remarks>Submitting this coin discloses the secret even if submission fails. It does not verify other swaps sharing its hash.</remarks>
    public ArkCoin ToNonInteractiveClaimCoin(string walletIdentifier, ArkVtxo vtxo, byte[] preimage)
    {
        ValidateNonInteractiveVtxo(vtxo);
        ValidatePreimage(preimage, Hash, nameof(preimage));
        var leaf = CreateNonInteractiveClaimScript();
        return new ArkCoin(walletIdentifier, this, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, null,
            new NonInteractiveSpendScript(leaf, NonInteractiveClaimArkadeScript, NonInteractiveClaim!.EmulatorPubKey,
                NonInteractiveClaim.ReceiverPkScript, NonInteractiveClaim.Strict?.Amount ?? 0),
            new WitScript(Op.GetPushOp(preimage)), null, null, vtxo.Swept, vtxo.Unrolled, vtxo.Assets);
    }

    /// <summary>Opens the BTC-only ninth, timelocked refund leaf without participant signatures or a preimage.</summary>
    /// <param name="walletIdentifier">The wallet tracking this lockup; no signer is required.</param>
    /// <param name="vtxo">An unspent, unswept output of this contract.</param>
    /// <returns>A signerless coin; submit only after chain maturity, with one pinned refund output per input.</returns>
    /// <exception cref="InvalidOperationException">The lockup is unusable or has no refund-without-receiver covenant leaf.</exception>
    public ArkCoin ToNonInteractiveRefundWithoutReceiverCoin(string walletIdentifier, ArkVtxo vtxo)
    {
        ValidateNonInteractiveVtxo(vtxo);
        var leaf = CreateNonInteractiveRefundWithoutReceiverScript();
        return new ArkCoin(walletIdentifier, this, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, null,
            new NonInteractiveSpendScript(leaf, NonInteractiveRefundArkadeScript, NonInteractiveRefund!.EmulatorPubKey,
                NonInteractiveRefund.SenderPkScript, 0),
            null, RefundLocktime, null, vtxo.Swept, vtxo.Unrolled, vtxo.Assets);
    }

    private void ValidateNonInteractiveVtxo(ArkVtxo vtxo)
    {
        if (Asset is not null || vtxo.Assets is { Count: > 0 })
            throw new InvalidOperationException("Non-interactive coin helpers are BTC-only; asset-bearing lockups are unsupported.");
        if (vtxo.IsSpent() || vtxo.Swept)
            throw new InvalidOperationException("The lockup VTXO is already spent or swept.");
        if (vtxo.TxOut.ScriptPubKey != GetScriptPubKey())
            throw new InvalidOperationException("The VTXO does not belong to this lockup contract.");
    }

    private sealed class NonInteractiveSpendScript(ScriptBuilder leaf, byte[] covenant, ECXOnlyPubKey emulator,
        byte[] destination, long minimum)
        : ScriptBuilder, IArkadeBoundScriptBuilder, IIndexedOutputScriptBuilder
    {
        public byte[] ArkadeScript => covenant;
        public WitScript? ArkadeScriptWitness => null;
        public IReadOnlyList<TaprootPubKey> EmulatorKeys { get; } = [new(emulator.ToBytes())];
        public override IEnumerable<Op> BuildScript() => leaf.BuildScript();

        public void ValidateIndexedOutput(TxOut input, TxOut output)
        {
            if (!output.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(destination)
                || output.Value < input.Value || output.Value.Satoshi < minimum)
                throw new InvalidOperationException("The indexed payout violates its committed destination or value floor.");
        }
    }
}

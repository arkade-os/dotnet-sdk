using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Onchain;

/// <summary>
/// The Bitcoin-L1 half of an onchain corridor swap: a two-leaf taproot HTLC.
/// </summary>
/// <remarks>
/// <para>
/// The internal key is the BIP-341 NUMS point, so there is no key-path spend, ever — the only ways
/// out are the two leaves, and both are visible to whoever holds the address.
/// </para>
/// <para>
/// Both sides of a swap derive this independently and must land on the same address, so every byte
/// here is part of the wire contract. <c>NArk.Tests/ArkadeIntents/Fixtures/onchain_htlc.json</c>
/// pins it against the counterparty's own derivation; a drift is an address one side cannot spend.
/// </para>
/// </remarks>
public sealed record OnchainHtlc(
    BitcoinAddress Address,
    Script PkScript,
    Script ClaimLeaf,
    Script RefundLeaf,
    ControlBlock ClaimControlBlock,
    ControlBlock RefundControlBlock,
    uint256 PaymentHash,
    LockTime RefundLocktime)
{
    /// <summary>
    /// Below this, a locktime means a block height rather than a timestamp (BIP-65).
    /// </summary>
    public const long LockTimeThreshold = 500_000_000;

    /// <summary>The preimage length the claim leaf accepts, in bytes.</summary>
    public const int PreimageSize = 32;

    /// <summary>
    /// Derive the HTLC both sides of the swap must agree on.
    /// </summary>
    /// <param name="paymentHash">SHA-256 of the preimage; the leaf commits to its HASH160.</param>
    /// <param name="claimKey">The x-only key that spends by revealing the preimage.</param>
    /// <param name="refundKey">The x-only key that spends after <paramref name="refundLocktime"/>.</param>
    /// <param name="refundLocktime">Absolute unix seconds; consensus matures it against median time past.</param>
    /// <param name="network">The L1 network the address is rendered for.</param>
    /// <returns>The derived HTLC.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="refundLocktime"/> is not a positive timestamp, or is height-shaped.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The claim leaf is <c>OP_SIZE 32 OP_EQUALVERIFY OP_HASH160 &lt;h160&gt; OP_EQUALVERIFY
    /// &lt;claimKey&gt; OP_CHECKSIG</c>, the refund leaf <c>&lt;locktime&gt; OP_CLTV OP_DROP
    /// &lt;refundKey&gt; OP_CHECKSIG</c>.
    /// </para>
    /// <para>
    /// The length check on the claim leaf is not decoration: without it the leaf accepts any witness
    /// value whose HASH160 matches, and this contract's preimage is exactly 32 bytes by
    /// construction — the same prefix real HTLC scripts carry, for the same reason.
    /// </para>
    /// </remarks>
    public static OnchainHtlc Derive(
        uint256 paymentHash,
        ECXOnlyPubKey claimKey,
        ECXOnlyPubKey refundKey,
        long refundLocktime,
        Network network)
    {
        if (refundLocktime <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refundLocktime), refundLocktime, "the refund locktime must be a positive unix timestamp");
        }

        // A bare number cannot say which of the two things it means, so a height-shaped value would
        // build a refund leaf maturing at block ~500 million rather than failing. Nothing downstream
        // detects it: the address is well formed and the funding confirms.
        if (refundLocktime < LockTimeThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refundLocktime), refundLocktime,
                $"the refund locktime is below the BIP-65 threshold ({LockTimeThreshold}) and would "
                + "be read as a block height, leaving the refund path dead for millennia");
        }

        var h160 = Hashes.RIPEMD160(paymentHash.ToBytes(false));

        var claim = new Script(
            OpcodeType.OP_SIZE,
            Op.GetPushOp(PreimageSize),
            OpcodeType.OP_EQUALVERIFY,
            OpcodeType.OP_HASH160,
            Op.GetPushOp(h160),
            OpcodeType.OP_EQUALVERIFY,
            Op.GetPushOp(claimKey.ToBytes()),
            OpcodeType.OP_CHECKSIG);

        var refund = new Script(
            Op.GetPushOp(refundLocktime),
            OpcodeType.OP_CHECKLOCKTIMEVERIFY,
            OpcodeType.OP_DROP,
            Op.GetPushOp(refundKey.ToBytes()),
            OpcodeType.OP_CHECKSIG);

        var claimLeaf = claim.ToTapScript(TapLeafVersion.C0);
        var refundLeaf = refund.ToTapScript(TapLeafVersion.C0);

        var builder = new TaprootBuilder()
            .AddLeaf(1, claimLeaf)
            .AddLeaf(1, refundLeaf);

        var internalKey = new TaprootInternalPubKey(NumsPoint);
        var info = builder.Finalize(internalKey);

        return new OnchainHtlc(
            info.OutputPubKey.GetAddress(network),
            info.OutputPubKey.ScriptPubKey,
            claim,
            refund,
            info.GetControlBlock(claimLeaf),
            info.GetControlBlock(refundLeaf),
            paymentHash,
            new LockTime((uint)refundLocktime));
    }

    /// <summary>
    /// The BIP-341 NUMS point: a key provably nobody holds, so the key path is unspendable.
    /// </summary>
    private static readonly byte[] NumsPoint = Convert.FromHexString(
        "50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0");
}

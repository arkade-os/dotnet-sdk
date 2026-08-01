using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Covclaim;
using NArk.Arkade.Crypto;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Pins our covenant-claim VHTLC against a real Boltz non-interactive swap.
/// </summary>
/// <remarks>
/// <para>
/// Every value below was captured from a live regtest exchange: Boltz built the
/// swap tree, and we recorded the leaf and lockup address it produced. The other
/// golden-vector tests in this folder prove we agree with the Go reference on how
/// to <em>build</em> the pieces; this one proves the assembled contract is the one
/// the counterparty will actually fund.
/// </para>
/// <para>
/// The address assertion is the load-bearing one. A wrong leaf, a wrong leaf
/// position, or a wrong tweak all yield a different taproot output key — and the
/// swap would fail at funding time with an address mismatch rather than anything
/// that points at the cause.
/// </para>
/// <para>
/// Captured against boltz <c>fulmine-v4-support</c> + fulmine <c>v0.4.0-rc.4</c> +
/// covclaimd <c>v0.0.1-rc.2</c>. Both the Boltz and ts-sdk changes were still
/// unmerged at capture time, so if this test starts failing, first check whether
/// the upstream leaf shape changed rather than assuming a regression here.
/// </para>
/// </remarks>
[TestFixture]
public class BoltzNonInteractiveClaimCompatibilityTests
{
    private const string ArkdSignerPubKey =
        "035c9b445a18f7b189d33cd2d51a919f5db6ed91bd769493bee4214c810a0912ca";

    private const string EmulatorPubKey =
        "02999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9";

    /// <summary>Boltz's refund key for this swap (x-only, as returned).</summary>
    private const string RefundPubKey =
        "300e4d77566fde5f34aa4d53605cc793de2630c24c1e941161937375e7606f9d";

    /// <summary>The key we sent as <c>claimPublicKey</c>.</summary>
    private const string ClaimPubKey =
        "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";

    /// <summary>The address we sent as <c>nonInteractiveClaim.claimAddress</c>.</summary>
    private const string ClaimAddress =
        "tark1qpwfk3z6rrmmrzwn8nfd2x53nawmdmv3h4mffya7uss5eqg2pyfv45l5aw22glmpker39hrxx5jparejl8k2gxv7xf4q8ygaw9drwdku0a3rk4";

    private const string PreimageHex =
        "7c0337ab60da79ab83f02d2ac3cb0cbc72e820e3aea549030b09e29692639103";

    // Boltz's timeoutBlockHeights for this swap.
    private const uint RefundLocktime = 157;
    private const uint UnilateralClaim = 105;
    private const uint UnilateralRefund = 110;
    private const uint UnilateralRefundWithoutReceiver = 115;

    /// <summary>Boltz's <c>swapTree.nonInteractiveClaimLeaf.output</c>.</summary>
    private const string BoltzNonInteractiveClaimLeaf =
        "a9149870059ca7c3f73bc4e654da3b8ce86c44c6017587" +
        "69205c9b445a18f7b189d33cd2d51a919f5db6ed91bd769493bee4214c810a0912caad" +
        "2080dc5fca12fab9d95a5fa9d919510c3559f1f4ada77383eb684c706292a620b7ac";

    /// <summary>Boltz's <c>lockupAddress</c> — the address it will actually fund.</summary>
    private const string BoltzLockupAddress =
        "tark1qpwfk3z6rrmmrzwn8nfd2x53nawmdmv3h4mffya7uss5eqg2pyfv5d7929ak7q5t9jdc2xpf48lngww908jud5vcjhpte6dkjm8273uzfe5a2u";

    private static TaprootPubKey BuildCovenantClaimKey()
    {
        var claimScriptPubKey = ArkAddress.Parse(ClaimAddress).ScriptPubKey;
        var arkadeScript = CovenantClaimScript.EnforcePayTo(claimScriptPubKey);

        var emulator = ECPubKey.Create(Convert.FromHexString(EmulatorPubKey));
        return ArkadeTweak.Tweak(new TaprootPubKey(emulator.ToXOnlyPubKey().ToBytes()), arkadeScript);
    }

    private static VHTLCContract BuildContract() =>
        new(
            KeyExtensions.ParseOutputDescriptor(ArkdSignerPubKey, Network.RegTest),
            KeyExtensions.ParseOutputDescriptor(RefundPubKey, Network.RegTest),
            KeyExtensions.ParseOutputDescriptor(ClaimPubKey, Network.RegTest),
            Convert.FromHexString(PreimageHex),
            new LockTime(RefundLocktime),
            new Sequence((int)UnilateralClaim),
            new Sequence((int)UnilateralRefund),
            new Sequence((int)UnilateralRefundWithoutReceiver),
            BuildCovenantClaimKey());

    [Test]
    public void CovenantClaimLeaf_MatchesBoltzNonInteractiveClaimLeaf()
    {
        var leaf = BuildContract().CreateCovenantClaimScript().Build().Script;

        Assert.That(Convert.ToHexString(leaf.ToBytes()).ToLowerInvariant(),
            Is.EqualTo(BoltzNonInteractiveClaimLeaf));
    }

    /// <summary>
    /// The whole tree, not just the new leaf: same leaves, same order, same tweak.
    /// If this passes, Boltz will fund exactly the contract we think it will.
    /// </summary>
    [Test]
    public void ContractAddress_MatchesBoltzLockupAddress()
    {
        Assert.That(BuildContract().GetArkAddress().ToString(false),
            Is.EqualTo(BoltzLockupAddress));
    }

    /// <summary>
    /// Without the covenant key we must reproduce a plain six-leaf VHTLC, which is a
    /// different address — the reason both sides have to agree on the leaf up front.
    /// </summary>
    [Test]
    public void WithoutCovenantKey_DoesNotMatchBoltzLockupAddress()
    {
        var plain = new VHTLCContract(
            KeyExtensions.ParseOutputDescriptor(ArkdSignerPubKey, Network.RegTest),
            KeyExtensions.ParseOutputDescriptor(RefundPubKey, Network.RegTest),
            KeyExtensions.ParseOutputDescriptor(ClaimPubKey, Network.RegTest),
            Convert.FromHexString(PreimageHex),
            new LockTime(RefundLocktime),
            new Sequence((int)UnilateralClaim),
            new Sequence((int)UnilateralRefund),
            new Sequence((int)UnilateralRefundWithoutReceiver));

        Assert.Multiple(() =>
        {
            Assert.That(plain.GetTapScriptList(), Has.Length.EqualTo(6));
            Assert.That(plain.GetArkAddress().ToString(false), Is.Not.EqualTo(BoltzLockupAddress));
        });
    }
}

using NArk.Abstractions.Extensions;
using NArk.ArkadeIntents.Lightning;
using NArk.Core;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// The relationships between the covenant's three unilateral leaves.
/// </summary>
/// <remarks>
/// <para>
/// These delays are <b>not carried on the wire</b>. Both sides derive them from the same operator
/// <c>/v1/info</c>, deliberately — a delay the solver could send is a delay the solver could
/// choose. The price is that the derivation itself is the wire format, so changing it on one side
/// is a protocol break wearing the clothes of a one-file refactor, and the only symptom is an
/// address that no longer matches the one being quoted.
/// </para>
/// <para>
/// Nothing asserted this before. The golden vectors took the three numbers as inputs and the
/// live-quote test re-implemented the rule inline, so the rule itself could drift with every test
/// still green — which is exactly how it drifted on the reference implementation. These are
/// properties rather than pinned values so that they hold at every operator delay, not just the
/// one a fixture happens to use.
/// </para>
/// </remarks>
[TestFixture]
public class UnilateralLadderTests
{
    /// <summary>Operator exit delays from the granularity floor up to a week.</summary>
    private static readonly uint[] Ladders = [512, 1536, 3600, 24 * 3600, 7 * 24 * 3600];

    [TestCaseSource(nameof(Ladders))]
    public void TheSoloRefund_OpensStrictlyAfterTheClaim(uint exitDelay)
    {
        // The one ordering that protects money: a funder who could refund alone before the
        // claimant can claim takes it from someone holding the preimage who did nothing wrong.
        var (claim, _, soloRefund) = Delays(exitDelay);

        Assert.That(soloRefund, Is.GreaterThan(claim));
    }

    [TestCaseSource(nameof(Ladders))]
    public void TheClaimant_GetsRealHeadroom_NotOneTick(uint exitDelay)
    {
        // Reaching a claim with the server gone means an unroll broadcast per chain step, each
        // waiting on a confirmation, then the CSV spend. One 512s tick never covered that, and a
        // gap that merely exists is not a gap that is enough.
        var (claim, _, soloRefund) = Delays(exitDelay);

        Assert.That(soloRefund - claim, Is.GreaterThanOrEqualTo(SwapScriptValues.SoloRefundHeadroomSeconds));
    }

    [TestCaseSource(nameof(Ladders))]
    public void TheTwoSignatureRefund_SitsLevelWithTheClaim(uint exitDelay)
    {
        // Neither party can spend that leaf alone, so separating it from the claim bought nothing
        // and spent headroom that does matter.
        var (claim, refund, _) = Delays(exitDelay);

        Assert.That(refund, Is.EqualTo(claim));
    }

    [TestCaseSource(nameof(Ladders))]
    public void EveryDelay_LandsOnABip68Boundary(uint exitDelay)
    {
        // A value off the boundary is silently re-rounded when encoded, which would move the leaf
        // without moving anything this test could otherwise see.
        var (claim, refund, soloRefund) = Delays(exitDelay);

        Assert.Multiple(() =>
        {
            Assert.That(claim % SwapScriptValues.SequenceGranularitySeconds, Is.Zero);
            Assert.That(refund % SwapScriptValues.SequenceGranularitySeconds, Is.Zero);
            Assert.That(soloRefund % SwapScriptValues.SequenceGranularitySeconds, Is.Zero);
        });
    }

    [TestCaseSource(nameof(Ladders))]
    public void NoLeaf_OpensBeforeTheOperatorsOwnExitDelay(uint exitDelay)
    {
        // A leaf reachable before the Arkade server's own exit delay is one whose spend the chain
        // is not yet ready to accept.
        var (claim, refund, soloRefund) = Delays(exitDelay);

        Assert.Multiple(() =>
        {
            Assert.That(claim, Is.GreaterThanOrEqualTo(exitDelay));
            Assert.That(refund, Is.GreaterThanOrEqualTo(exitDelay));
            Assert.That(soloRefund, Is.GreaterThanOrEqualTo(exitDelay));
        });
    }

    [Test]
    public void TheHeadroom_MatchesTheReferenceSolver()
    {
        // Pinned as a number because the counterparty pins it as a number. If theirs moves, this
        // fails here rather than at an address comparison against a live quote.
        Assert.That(SwapScriptValues.SoloRefundHeadroomSeconds, Is.EqualTo(8 * 512));
    }

    [Test]
    public void ATimeBasedDelay_BelowTheGranularity_IsRefused()
    {
        // Sub-granularity seconds encode to nothing at all — this is what a block count looks like
        // if it ever reached the derivation dressed as seconds. The transports already turn a value
        // under 512 into a BLOCK sequence, caught by the test above, so this guards the remaining
        // way in: an ArkServerInfo assembled by hand.
        var subGranularity = TestServerInfo.With(new Sequence(TimeSpan.FromSeconds(256)));

        Assert.Throws<InvalidOperationException>(
            () => LightningCorridor.UnilateralDelays(subGranularity));
    }

    [Test]
    public void AnExitDelay_ThatLeavesNoRoomForTheHeadroom_IsRefused()
    {
        // The ceiling has to leave room for what gets stacked on top, not just for the base:
        // otherwise the base encodes and the solo refund silently does not.
        var justOverTheCeiling = 0xffff * SwapScriptValues.SequenceGranularitySeconds
                                 - SwapScriptValues.SoloRefundHeadroomSeconds
                                 + SwapScriptValues.SequenceGranularitySeconds;

        Assert.Throws<InvalidOperationException>(() => Delays(justOverTheCeiling));
    }

    [Test]
    public void ABlockBasedOperator_IsRefusedOutright()
    {
        // Block-interval variance is far too wide to hold a Lightning deadline against.
        var info = TestServerInfo.With(new Sequence(5));

        Assert.Throws<InvalidOperationException>(() => LightningCorridor.UnilateralDelays(info));
    }

    private static (uint Claim, uint Refund, uint SoloRefund) Delays(uint exitDelaySeconds) =>
        LightningCorridor.UnilateralDelays(TestServerInfo.WithSeconds(exitDelaySeconds));


}

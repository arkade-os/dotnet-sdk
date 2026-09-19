using NArk.ArkadeIntents.Lightning;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// The one rung of the CSV ladder a send quote is allowed to set, and every bound it is held to.
/// </summary>
/// <remarks>
/// <para>
/// The other two rungs are derived on both sides from the operator's own <c>/v1/info</c>, so
/// neither party can move them. This one cannot be derived: it has to reach past the operator's
/// base whenever the quoted <c>refund_locktime</c> is further away than the base ladder's fixed
/// headroom, which on a Lightning send is essentially always. So the solver sizes it and publishes
/// it, and what protects the client is this check rather than a matching derivation.
/// </para>
/// <para>
/// Each bound below fails differently and all of them fail quietly, which is why they are pinned
/// individually: an unencodable delay is rounded by BIP68 into a leaf that times something nobody
/// agreed to, and a delay that is merely too short is a perfectly valid script whose only defect is
/// that it lets the funder take the deposit back while a claimant still holds a live preimage.
/// </para>
/// </remarks>
[TestFixture]
public class NegotiatedSoloRefundTests
{
    /// <summary>A 512s operator: claim and two-party refund at 512, base solo refund at 4608.</summary>
    private static readonly (uint Claim, uint Refund, uint RefundWithoutReceiver) Base = (512, 512, 4608);

    private const long Now = 1_800_000_000;

    [Test]
    public void AQuoteWithoutTheField_FallsBackToTheBaseLadder()
    {
        // A solver predating the field derives the base ladder, so a client that refused here would
        // be dropping a deployment it can still swap with — and the address comparison that follows
        // is what actually catches a disagreement.
        var resolved = LightningCorridor.ResolveSoloRefundDelay(
            null, Base, refundLocktime: Now + 3600, now: Now);

        Assert.That(resolved, Is.EqualTo(Base.RefundWithoutReceiver));
    }

    [Test]
    public void AStretchedDelay_IsAdopted_NotSecondGuessed()
    {
        // The whole point of the field: a horizon of a day needs a solo rung far past the base
        // ladder's 4608s, and the client builds the solver's number rather than its own.
        const long horizon = 24 * 3600;
        const long stretched = 86_528; // 169 * 512, the first unit at or above the horizon

        var resolved = LightningCorridor.ResolveSoloRefundDelay(
            stretched, Base, refundLocktime: Now + horizon, now: Now);

        Assert.That(resolved, Is.EqualTo(stretched));
    }

    [Test]
    public void ADelay_ThatOpensBeforeItsOwnRefundLocktime_IsRefused()
    {
        // The theft window itself: the funder's solo path would open while the claimant, holding
        // the preimage, still has a claim the covenant says is live.
        Assert.Throws<QuotedDelayRejectedException>(() => LightningCorridor.ResolveSoloRefundDelay(
            Base.RefundWithoutReceiver, Base, refundLocktime: Now + 24 * 3600, now: Now));
    }

    [Test]
    public void ADelay_BelowTheClaimRung_IsRefused()
    {
        // Same theft, reached from the other side: a solo refund that opens before the claim leaf
        // does is one the claimant can never win, whatever the absolute deadline says.
        Assert.Throws<QuotedDelayRejectedException>(() => LightningCorridor.ResolveSoloRefundDelay(
            256, (1024, 1024, 5120), refundLocktime: Now, now: Now));
    }

    [TestCase(4000)]
    [TestCase(513)]
    public void ADelay_OffTheBip68Grid_IsRefused(long quoted)
    {
        // BIP68 counts 512-second units, so a value between them is rounded when encoded. The leaf
        // would then time something other than the number both sides agreed on, and nothing local
        // would say so.
        Assert.Throws<QuotedDelayRejectedException>(() => LightningCorridor.ResolveSoloRefundDelay(
            quoted, Base, refundLocktime: Now, now: Now));
    }

    [TestCase(0)]
    [TestCase(-512)]
    public void ANonPositiveDelay_IsRefused(long quoted)
    {
        // A zero delay is a solo refund with no wait at all — the leaf exists, the protection does
        // not.
        Assert.Throws<QuotedDelayRejectedException>(() => LightningCorridor.ResolveSoloRefundDelay(
            quoted, Base, refundLocktime: Now, now: Now));
    }

    [Test]
    public void ADelay_PastWhatBip68Encodes_IsRefused()
    {
        // Caught here rather than at the script builder, where it would surface as an encoding that
        // silently wrapped.
        var justOver = (0xffff + 1L) * SwapScriptValues.SequenceGranularitySeconds;

        Assert.Throws<QuotedDelayRejectedException>(() => LightningCorridor.ResolveSoloRefundDelay(
            justOver, Base, refundLocktime: Now, now: Now));
    }

    [Test]
    public void TheRefusal_CarriesTheOffendingValue()
    {
        // A caller deciding whether to retry with another solver needs the number, not prose.
        var refusal = Assert.Throws<QuotedDelayRejectedException>(() =>
            LightningCorridor.ResolveSoloRefundDelay(4000, Base, refundLocktime: Now, now: Now));

        Assert.That(refusal!.Quoted, Is.EqualTo(4000));
    }
}

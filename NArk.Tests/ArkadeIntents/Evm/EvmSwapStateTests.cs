using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;

namespace NArk.Tests.ArkadeIntents.Evm;

[TestFixture]
public class EvmSwapStateTests
{
    [TestCase(false, ArkadeSwapIntentStatus.Resolved)]
    [TestCase(true, ArkadeSwapIntentStatus.Claimable)]
    public void SpendingLOnlyMakesTheVerifiedEvmClaimActionableWhenItsWitnessRevealsP(
        bool preimageRevealed, ArkadeSwapIntentStatus expected) => Assert.That(
        ArkadeSwapStateMachine.Next(
            ArkadeSwapIntentType.BtcToEvm,
            ArkadeSwapIntentStatus.Pending,
            new SwapObservation(true, false, 100, 200, preimageRevealed)),
        Is.EqualTo(expected));

    [Test]
    public void AnExpiredUnspentLBecomesRefundable() => Assert.Multiple(() =>
    {
        var next = ArkadeSwapStateMachine.NextOnClock(
            ArkadeSwapIntentType.BtcToEvm, ArkadeSwapIntentStatus.Pending, 200, 200);
        Assert.That(next, Is.EqualTo(ArkadeSwapIntentStatus.Refundable));
        Assert.That(ArkadeSwapStateMachine.ActionFor(ArkadeSwapIntentType.BtcToEvm, next!.Value),
            Is.EqualTo(ArkadeIntentAction.RefundSend));
    });
}

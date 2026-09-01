using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// Which swaps an automation may act on by itself.
/// </summary>
/// <remarks>
/// The interesting assertions here are the negative ones. A policy that acts too little leaves money
/// recoverable; a policy that acts too much destroys a swap someone deliberately started. The second
/// is worse and quieter, so the cases that must stay untouched are pinned as carefully as the two
/// that must not.
/// </remarks>
[TestFixture]
public class ArkadeIntentPolicyTests
{
    [Test]
    public void AFundedReceiveSwap_IsClaimed()
    {
        // The solver already paid out and only our preimage moves it, on a clock.
        Assert.That(
            ArkadeIntentPolicy.NextAction(
                Intent(ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Claimable)),
            Is.EqualTo(ArkadeIntentAction.ClaimReceive));
    }

    [Test]
    public void ASendSwapPastItsLocktime_IsRefunded()
    {
        // Pays our own address, needs no counterparty, and nobody else is coming to push it.
        Assert.That(
            ArkadeIntentPolicy.NextAction(
                Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Refundable)),
            Is.EqualTo(ArkadeIntentAction.RefundSend));
    }

    [Test]
    public void APendingAssetSwap_IsLeftAlone()
    {
        // It is waiting to be filled, which is what was asked for. Cancelling it automatically would
        // destroy the thing the automation exists to look after.
        Assert.That(
            ArkadeIntentPolicy.NextAction(
                Intent(ArkadeSwapIntentType.BtcToAsset, ArkadeSwapIntentStatus.Pending)),
            Is.EqualTo(ArkadeIntentAction.None));
    }

    [Test]
    public void APendingSendSwap_IsLeftAlone()
    {
        // RefundSwap would accept it, but before the locktime the covenant refund is unspendable and
        // the solver may still fill. Acting here races a swap that is working.
        Assert.That(
            ArkadeIntentPolicy.NextAction(
                Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Pending)),
            Is.EqualTo(ArkadeIntentAction.None));
    }

    [Test]
    public void AReceiveSwapStillWaitingForFunding_IsLeftAlone()
    {
        Assert.That(
            ArkadeIntentPolicy.NextAction(
                Intent(ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Pending)),
            Is.EqualTo(ArkadeIntentAction.None));
    }

    [Test]
    public void AClaimableStatusOnTheWrongCorridor_IsLeftAlone()
    {
        // Claimable is only meaningful on the receive leg. Acting on it elsewhere would mean
        // spending a leaf the corridor does not even build.
        Assert.That(
            ArkadeIntentPolicy.NextAction(
                Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Claimable)),
            Is.EqualTo(ArkadeIntentAction.None));
    }

    [TestCase(ArkadeSwapIntentStatus.Fulfilled)]
    [TestCase(ArkadeSwapIntentStatus.Cancelled)]
    [TestCase(ArkadeSwapIntentStatus.Cancelling)]
    [TestCase(ArkadeSwapIntentStatus.Resolved)]
    public void ASwapThatIsOverOrMidSpend_IsLeftAlone(ArkadeSwapIntentStatus status)
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ArkadeIntentPolicy.NextAction(Intent(ArkadeSwapIntentType.LightningToBtc, status)),
                Is.EqualTo(ArkadeIntentAction.None));
            Assert.That(
                ArkadeIntentPolicy.NextAction(Intent(ArkadeSwapIntentType.BtcToLightning, status)),
                Is.EqualTo(ArkadeIntentAction.None));
        });
    }

    private static ArkadeSwapIntent Intent(ArkadeSwapIntentType type, ArkadeSwapIntentStatus status) => new()
    {
        Id = "swap-1",
        WalletId = "wallet-1",
        Type = type,
        OfferAmount = Money.Satoshis(50_000),
        WantAmount = Money.Satoshis(50_000),
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        SwapPkScript = "5120" + new string('a', 64),
        SwapAddress = "tark1example",
    };
}

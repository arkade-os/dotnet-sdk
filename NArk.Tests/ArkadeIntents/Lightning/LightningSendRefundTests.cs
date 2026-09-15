using NArk.Abstractions.Extensions;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// The covenant refund: where it may pay, and when we may push it.
/// </summary>
[TestFixture]
public class LightningSendRefundTests
{
    private const long Locktime = 1_800_605_184;

    /// <summary>
    /// The covenant admits exactly one destination, and a refund paying anywhere else is invalid —
    /// so the address must come back out of the funded contract, never from whatever the wallet
    /// would hand out today. A fresh receive address here would produce a spend the emulator
    /// refuses to co-sign, with the deposit still stuck.
    /// </summary>
    [Test]
    public void RefundAddress_IsRecoveredFromTheContract_NotChosenAfresh()
    {
        var serverKey = Key(3);
        var refundPkScript = P2tr(Key(5));
        var contract = new VHTLCv2Contract(
            Descriptor(serverKey),
            sender: Descriptor(Key(7)),
            receiver: Descriptor(Key(1)),
            new uint160(new byte[20], false),
            new LockTime(1_800_000_000),
            new Sequence(TimeSpan.FromSeconds(4096)),
            new Sequence(TimeSpan.FromSeconds(4608)),
            new Sequence(TimeSpan.FromSeconds(5120)),
            nonInteractiveClaim: new VHTLCv2NonInteractiveClaim(
                P2tr(Key(2)), NBitcoin.Secp256k1.ECXOnlyPubKey.Create(Key(9))),
            nonInteractiveRefund: new VHTLCv2NonInteractiveRefund(
                refundPkScript, NBitcoin.Secp256k1.ECXOnlyPubKey.Create(Key(9))));

        var recovered = LightningIntentsClient.RefundAddressOf(
            contract, NBitcoin.Secp256k1.ECXOnlyPubKey.Create(serverKey));

        // Same scriptPubKey the maker committed to at funding time, prefix and all.
        Assert.That(recovered.ScriptPubKey.ToBytes(), Is.EqualTo(refundPkScript));
    }

    [Test]
    public void ARefundBeforeTheDeadline_IsRefusedWithTimeRemaining()
    {
        var refunder = Refunder(Swap(), now: Locktime - 60);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => refunder.RefundSwap("swap-1"));

        Assert.That(ex!.Message, Does.Contain("60s"));
    }

    [Test]
    public void AnAssetSwap_IsNotRefundableThisWay()
    {
        // The asset directions have a cancel path, not a covenant refund; sending one through here
        // would build a spend against a leaf their program does not have.
        var swap = Swap();
        swap.Type = ArkadeSwapIntentType.BtcToAsset;
        var refunder = Refunder(swap, now: Locktime + 1);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => refunder.RefundSwap("swap-1"));

        Assert.That(ex!.Message, Does.Contain("not a corridor swap this refund applies to"));
    }

    [Test]
    public void AnOffBoard_IsRefundableThisWay()
    {
        // The off-board settles into the same VHTLCv2 covenant as the Lightning send leg and takes
        // the same `refundWithoutReceiver` leaf, so this is its refund too — and it is the ONLY one
        // it has once the L1 window has shut. It used to be turned away here by a corridor check,
        // which left the action the policy routes to it throwing every time it fired.
        var swap = Swap();
        swap.Type = ArkadeSwapIntentType.BtcToOnchain;
        // Past the median-time-past lag too, so the corridor check is the only gate left that could
        // turn it away.
        var refunder = Refunder(swap, now: Locktime + 7201);

        // The stubs cannot carry a spend through, so it still fails — but on the machinery rather
        // than on the door. Asserting the absence of the corridor refusal is what pins the fix:
        // "throws nothing" was never reachable here, and "throws" alone passed before it too.
        var ex = Assert.CatchAsync(() => refunder.RefundSwap("swap-1"));

        Assert.That(ex!.Message, Does.Not.Contain("not a corridor swap this refund applies to"));
    }

    [Test]
    public void AnAlreadySettledSwap_IsNotRefunded()
    {
        var swap = Swap();
        swap.Status = ArkadeSwapIntentStatus.Fulfilled;
        var refunder = Refunder(swap, now: Locktime + 1);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => refunder.RefundSwap("swap-1"));

        Assert.That(ex!.Message, Does.Contain("not awaiting a refund"));
    }

    [Test]
    public void AFailedRefund_RollsTheStatusBack()
    {
        // Nothing else can be reached in this test's stubs, so the attempt fails after the status
        // was moved — the swap must not be left stranded in Cancelling, where neither the monitor
        // nor a later retry would touch it.
        var swap = Swap();
        var storage = Substitute.For<IArkadeIntentStorage>();
        storage.GetArkadeSwapIntents(id: Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns([swap]);
        var refunder = Refunder(swap, Locktime + 1, storage);

        // The stubs cannot carry the spend through, so whatever it fails on, the guarantee under
        // test is the same: the status does not stay parked in Cancelling.
        Assert.CatchAsync(() => refunder.RefundSwap("swap-1"));
        Assert.That(swap.Status, Is.EqualTo(ArkadeSwapIntentStatus.Refundable));
    }

    private static LightningIntentsClient Refunder(
        ArkadeSwapIntent swap, long now, IArkadeIntentStorage? storage = null)
    {
        if (storage is null)
        {
            storage = Substitute.For<IArkadeIntentStorage>();
            storage.GetArkadeSwapIntents(id: Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
                .Returns([swap]);
        }

        return new LightningIntentsClient(
            Substitute.For<IClientTransport>(),
            Substitute.For<IContractService>(),
            Substitute.For<ISpendingService>(),
            storage,
            Substitute.For<IContractStorage>(),
            Substitute.For<IVtxoStorage>(),
            Substitute.For<IWalletProvider>(),
            // Named, so a future parameter cannot silently land in the clock's position.
            time: new FixedClock(now));
    }

    private static ArkadeSwapIntent Swap() => new()
    {
        Id = "swap-1",
        WalletId = "wallet-1",
        Type = ArkadeSwapIntentType.BtcToLightning,
        OfferAmount = Money.Satoshis(50_000),
        WantAmount = Money.Satoshis(50_000),
        Status = ArkadeSwapIntentStatus.Refundable,
        CreatedAt = DateTimeOffset.UtcNow,
        SwapPkScript = "5120" + new string('a', 64),
        SwapAddress = "ark1qlockup",
        RefundLocktime = Locktime,
    };

    private static byte[] Key(byte fill) =>
        NBitcoin.Secp256k1.ECPrivKey.Create(Enumerable.Repeat(fill, 32).ToArray())
            .CreateXOnlyPubKey().ToBytes();

    private static byte[] P2tr(byte[] program) => [0x51, 0x20, .. program];

    private static OutputDescriptor Descriptor(byte[] xOnly) =>
        KeyExtensions.ParseOutputDescriptor(
            Convert.ToHexString([(byte)0x02, .. xOnly]).ToLowerInvariant(), Network.RegTest);

    private sealed class FixedClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}

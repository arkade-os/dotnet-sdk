using NArk.Abstractions.Extensions;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
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
public class LightningSwapRefundTests
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
        var contract = CovenantSwapProgram.BuildContract(
            new CovenantSwapParams(
                Receiver: Key(1),
                PreimageHash: new byte[20],
                RefundLocktime: 1_800_000_000,
                ClaimDelay: 4096,
                EmulatorPubkey: Key(9),
                RefundPkScript: refundPkScript),
            Descriptor(serverKey));

        var recovered = LightningSwapClient.RefundAddressOf(
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

        Assert.That(ex!.Message, Does.Contain("not a Lightning swap"));
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
        storage.GetArkadeSwapIntents(cancellationToken: Arg.Any<CancellationToken>())
            .Returns([swap]);
        var refunder = Refunder(swap, Locktime + 1, storage);

        // The stubs cannot carry the spend through, so whatever it fails on, the guarantee under
        // test is the same: the status does not stay parked in Cancelling.
        Assert.CatchAsync(() => refunder.RefundSwap("swap-1"));
        Assert.That(swap.Status, Is.EqualTo(ArkadeSwapIntentStatus.Refundable));
    }

    private static LightningSwapClient Refunder(
        ArkadeSwapIntent swap, long now, IArkadeIntentStorage? storage = null)
    {
        if (storage is null)
        {
            storage = Substitute.For<IArkadeIntentStorage>();
            storage.GetArkadeSwapIntents(cancellationToken: Arg.Any<CancellationToken>())
                .Returns([swap]);
        }

        return new LightningSwapClient(
            Substitute.For<IClientTransport>(),
            Substitute.For<IEmulatorProvider>(),
            Substitute.For<IContractService>(),
            Substitute.For<ISpendingService>(),
            storage,
            Substitute.For<IContractStorage>(),
            Substitute.For<IVtxoStorage>(),
            Substitute.For<IWalletProvider>(),
            new FixedClock(now));
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
        OfferHex = "",
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

using NArk.Abstractions.VTXOs;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Common;
using NArk.Swaps.Models;
using NBitcoin;
using NSubstitute;
using static NArk.Swaps.Boltz.BoltzSwapStatus;

namespace NArk.Tests;

[TestFixture]
public class SubmarineTerminalHistoryTests
{
    [TestCase(InvoiceFailedToPay)]
    [TestCase(SwapExpired)]
    public async Task FinalStatusWithoutCanonicalHistory_MarksSwapFailed(string boltzStatus)
    {
        var (provider, swapStorage, vtxoStorage) = CreateProvider([]);
        var swap = MakeSwap();

        await provider.RequestSubmarineCoopRefund(
            swap,
            new SwapStatusResponse { Status = boltzStatus });

        await swapStorage.Received(1).SaveSwap(
            swap.WalletId,
            Arg.Is<ArkSwap>(saved =>
                saved.Status == ArkSwapStatus.Failed &&
                saved.FailReason != null &&
                saved.FailReason.Contains("no canonical lockup")),
            Arg.Any<CancellationToken>());
        await AssertHistoryWasRequested(vtxoStorage);
    }

    [Test]
    public async Task SpentCanonicalHistory_IsNotClassifiedAsUnfunded()
    {
        var swap = MakeSwap();
        var spentLockup = MakeVtxo(swap.ExpectedAmount, spent: true);
        var (provider, swapStorage, vtxoStorage) = CreateProvider([spentLockup]);

        await provider.RequestSubmarineCoopRefund(
            swap,
            new SwapStatusResponse { Status = InvoiceFailedToPay });

        await swapStorage.DidNotReceiveWithAnyArgs()
            .SaveSwap(default!, default!, default);
        await AssertHistoryWasRequested(vtxoStorage);
    }

    [Test]
    public async Task NonFinalStatusWithoutHistory_RemainsActive()
    {
        var (provider, swapStorage, _) = CreateProvider([]);

        await provider.RequestSubmarineCoopRefund(
            MakeSwap(),
            new SwapStatusResponse { Status = InvoiceExpired });

        await swapStorage.DidNotReceiveWithAnyArgs()
            .SaveSwap(default!, default!, default);
    }

    [TestCase(InvoiceFailedToPay, true)]
    [TestCase(SwapExpired, true)]
    [TestCase(InvoiceExpired, false)]
    [TestCase(TransactionLockupFailed, false)]
    public void FinalityMatchesBoltzLifecycle(string status, bool expected)
    {
        Assert.That(BoltzOperationClassifier.IsFinalSubmarineRefundStatus(status), Is.EqualTo(expected));
    }

    private static (
        BoltzSwapProvider Provider,
        ISwapStorage SwapStorage,
        IVtxoStorage VtxoStorage) CreateProvider(IReadOnlyCollection<ArkVtxo> history)
    {
        var transport = Substitute.For<IClientTransport>();
        var vtxoStorage = Substitute.For<IVtxoStorage>();
        var swapStorage = Substitute.For<ISwapStorage>();

        transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(System.Linq.AsyncEnumerable.Empty<ArkVtxo>());
        vtxoStorage.GetVtxos(
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<OutPoint>?>(),
                Arg.Any<string[]?>(),
                true,
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(history);

        var provider = BoltzTestFixture.CreateProvider(
            clientTransport: transport,
            vtxoStorage: vtxoStorage,
            swapStorage: swapStorage);

        return (provider, swapStorage, vtxoStorage);
    }

    private static Task AssertHistoryWasRequested(IVtxoStorage vtxoStorage) =>
        vtxoStorage.Received(1).GetVtxos(
            Arg.Is<IReadOnlyCollection<string>?>(scripts =>
                scripts != null && scripts.Single() == "swap-script"),
            Arg.Any<IReadOnlyCollection<OutPoint>?>(),
            Arg.Any<string[]?>(),
            true,
            Arg.Any<string?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());

    private static ArkSwap MakeSwap() =>
        new(
            "swap-id",
            "wallet-id",
            ArkSwapType.Submarine,
            "invoice",
            50_000,
            "swap-script",
            "address",
            ArkSwapStatus.Pending,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "hash");

    private static ArkVtxo MakeVtxo(long amount, bool spent) =>
        new(
            "swap-script",
            new string('a', 64),
            0,
            (ulong)amount,
            spent ? new string('b', 64) : null,
            null,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            null);

}

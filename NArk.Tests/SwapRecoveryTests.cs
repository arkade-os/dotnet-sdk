using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.CoinSelector;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Unit tests for the recovery-inspection helpers added to
/// <see cref="SwapsManagementService"/> — they sit on top of swap and
/// vtxo storage and don't talk to any provider, so a thin set of
/// substitutes is enough to drive every status branch.
/// </summary>
[TestFixture]
public class SwapRecoveryTests
{
    private const string WalletId = "test-wallet";
    private const string SwapId = "swap-abc";
    private const string ContractScript = "5120abcdef";

    private ISwapStorage _swapStorage = null!;
    private IVtxoStorage _vtxoStorage = null!;
    private IClientTransport _transport = null!;
    private SwapsManagementService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _swapStorage = Substitute.For<ISwapStorage>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _transport = Substitute.For<IClientTransport>();

        // Default: arkd snapshot returns nothing — individual tests override.
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<HashSet<string>>(), Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable<ArkVtxo>());

        _sut = BuildService();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_sut is not null) await _sut.DisposeAsync();
    }

    [Test]
    public async Task InspectSwapRecovery_SwapNotFound_ReportsSwapNotFound()
    {
        _swapStorage.GetSwaps(walletIds: Arg.Any<string[]?>(),
                swapIds: Arg.Any<string[]?>(),
                active: Arg.Any<bool?>(),
                swapTypes: Arg.Any<ArkSwapType[]?>(),
                status: Arg.Any<ArkSwapStatus[]?>(),
                contractScripts: Arg.Any<string[]?>(),
                hashes: Arg.Any<string[]?>(),
                invoices: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArkSwap>());

        var info = await _sut.InspectSwapRecoveryAsync(WalletId, "missing-swap");

        Assert.That(info.Status, Is.EqualTo(SwapRecoveryStatus.SwapNotFound));
        Assert.That(info.Swap, Is.Null);
    }

    [Test]
    public async Task InspectSwapRecovery_SettledSwap_ReportsAlreadySettled()
    {
        SetupSwap(ArkSwapStatus.Settled);

        var info = await _sut.InspectSwapRecoveryAsync(WalletId, SwapId);

        Assert.That(info.Status, Is.EqualTo(SwapRecoveryStatus.AlreadySettled));
        Assert.That(info.Swap?.Status, Is.EqualTo(ArkSwapStatus.Settled));
    }

    [Test]
    public async Task InspectSwapRecovery_RefundedSwap_ReportsAlreadyRefunded()
    {
        SetupSwap(ArkSwapStatus.Refunded);

        var info = await _sut.InspectSwapRecoveryAsync(WalletId, SwapId);

        Assert.That(info.Status, Is.EqualTo(SwapRecoveryStatus.AlreadyRefunded));
    }

    [Test]
    public async Task InspectSwapRecovery_PendingSwap_ReportsStillPending_NotRecoverable()
    {
        // Pending = mid-flight swap. VTXOs at its contract script are the
        // live lockup, NOT stranded funds. Without the guard, the method
        // would fall through and report Recoverable (with VTXOs) or NoFunds
        // (without) — both wrong for a working swap.
        SetupSwap(ArkSwapStatus.Pending);
        _vtxoStorage.GetVtxos(scripts: Arg.Any<IReadOnlyCollection<string>?>(),
                outpoints: Arg.Any<IReadOnlyCollection<NBitcoin.OutPoint>?>(),
                walletIds: Arg.Any<string[]?>(),
                includeSpent: Arg.Any<bool>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns([new ArkVtxo(
                Script: ContractScript,
                TransactionId: new string('a', 64),
                TransactionOutputIndex: 0,
                Amount: 50_000,
                SpentByTransactionId: null,
                SettledByTransactionId: null,
                Swept: false,
                CreatedAt: DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                ExpiresAtHeight: null)]);

        var info = await _sut.InspectSwapRecoveryAsync(WalletId, SwapId);

        Assert.That(info.Status, Is.EqualTo(SwapRecoveryStatus.StillPending),
            "Pending swaps must report StillPending — they're mid-flight, not stranded");
        // The arkd snapshot probe must NOT run for Pending swaps either (no
        // reason to talk to the network for a swap we know isn't recovery
        // material yet).
        await _transport.DidNotReceiveWithAnyArgs()
            .GetVtxoByScriptsAsSnapshot(default!, default).ToListAsync();
    }

    [Test]
    public async Task InspectSwapRecovery_FailedSwapWithNoVtxos_ReportsNoFunds()
    {
        SetupSwap(ArkSwapStatus.Failed);
        // Empty arkd snapshot + empty local vtxos
        _vtxoStorage.GetVtxos(scripts: Arg.Any<IReadOnlyCollection<string>?>(),
                outpoints: Arg.Any<IReadOnlyCollection<NBitcoin.OutPoint>?>(),
                walletIds: Arg.Any<string[]?>(),
                includeSpent: Arg.Any<bool>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArkVtxo>());

        var info = await _sut.InspectSwapRecoveryAsync(WalletId, SwapId);

        Assert.That(info.Status, Is.EqualTo(SwapRecoveryStatus.NoFunds));
        Assert.That(info.VtxoCount, Is.EqualTo(0));
        Assert.That(info.AmountSats, Is.EqualTo(0));
    }

    [Test]
    public async Task InspectSwapRecovery_FailedSwapWithUnspentVtxos_ReportsRecoverable()
    {
        SetupSwap(ArkSwapStatus.Failed);
        var vtxo = MakeVtxo(amountSats: 12345);
        _vtxoStorage.GetVtxos(scripts: Arg.Any<IReadOnlyCollection<string>?>(),
                outpoints: Arg.Any<IReadOnlyCollection<NBitcoin.OutPoint>?>(),
                walletIds: Arg.Any<string[]?>(),
                includeSpent: Arg.Any<bool>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new[] { vtxo });

        var info = await _sut.InspectSwapRecoveryAsync(WalletId, SwapId);

        Assert.That(info.Status, Is.EqualTo(SwapRecoveryStatus.Recoverable));
        Assert.That(info.VtxoCount, Is.EqualTo(1));
        Assert.That(info.AmountSats, Is.EqualTo(12345));
    }

    [Test]
    public async Task InspectSwapRecovery_ArkdSnapshotThrows_ReportsInspectionError()
    {
        SetupSwap(ArkSwapStatus.Failed);
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<HashSet<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingAsyncEnumerable<ArkVtxo>(new InvalidOperationException("arkd unreachable")));

        var info = await _sut.InspectSwapRecoveryAsync(WalletId, SwapId);

        Assert.That(info.Status, Is.EqualTo(SwapRecoveryStatus.InspectionError));
        Assert.That(info.Error, Does.Contain("arkd unreachable"));
    }

    [Test]
    public async Task ScanRecoverableSwaps_SkipsPendingSwaps()
    {
        var pendingSwap = MakeSwap(SwapId + "-1", ArkSwapStatus.Pending);
        var failedSwap = MakeSwap(SwapId + "-2", ArkSwapStatus.Failed);
        _swapStorage.GetSwaps(walletIds: Arg.Any<string[]?>(),
                swapIds: Arg.Any<string[]?>(), active: Arg.Any<bool?>(),
                swapTypes: Arg.Any<ArkSwapType[]?>(),
                status: Arg.Any<ArkSwapStatus[]?>(),
                contractScripts: Arg.Any<string[]?>(),
                hashes: Arg.Any<string[]?>(),
                invoices: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(), take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Bulk lookup for ScanRecoverableSwapsAsync (no swapIds filter)
                var swapIds = callInfo.ArgAt<string[]?>(1);
                if (swapIds is null) return new[] { pendingSwap, failedSwap };
                // Per-swap lookup from InspectSwapRecoveryAsync
                if (swapIds.Length == 1 && swapIds[0] == failedSwap.SwapId) return new[] { failedSwap };
                return Array.Empty<ArkSwap>();
            });
        _vtxoStorage.GetVtxos(scripts: Arg.Any<IReadOnlyCollection<string>?>(),
                outpoints: Arg.Any<IReadOnlyCollection<NBitcoin.OutPoint>?>(),
                walletIds: Arg.Any<string[]?>(),
                includeSpent: Arg.Any<bool>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArkVtxo>());

        var report = await _sut.ScanRecoverableSwapsAsync(WalletId);

        // Only the failed swap is inspected — pending is skipped entirely.
        Assert.That(report, Has.Count.EqualTo(1));
        Assert.That(report[0].SwapId, Is.EqualTo(failedSwap.SwapId));
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private void SetupSwap(ArkSwapStatus status)
    {
        var swap = MakeSwap(SwapId, status);
        _swapStorage.GetSwaps(walletIds: Arg.Any<string[]?>(),
                swapIds: Arg.Any<string[]?>(), active: Arg.Any<bool?>(),
                swapTypes: Arg.Any<ArkSwapType[]?>(),
                status: Arg.Any<ArkSwapStatus[]?>(),
                contractScripts: Arg.Any<string[]?>(),
                hashes: Arg.Any<string[]?>(),
                invoices: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(), take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new[] { swap });
    }

    private static ArkSwap MakeSwap(string swapId, ArkSwapStatus status) => new(
        SwapId: swapId,
        WalletId: WalletId,
        SwapType: ArkSwapType.Submarine,
        Invoice: "lnbcrt...",
        ExpectedAmount: 10_000,
        ContractScript: ContractScript,
        Address: "tark1...",
        Status: status,
        FailReason: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Hash: "abcd");

    private static ArkVtxo MakeVtxo(long amountSats) => new(
        Script: ContractScript,
        TransactionId: new string('a', 64),
        TransactionOutputIndex: 0u,
        Amount: (ulong)amountSats,
        SpentByTransactionId: null,
        SettledByTransactionId: null,
        Swept: false,
        CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(30),
        ExpiresAtHeight: null);

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<T> ThrowingAsyncEnumerable<T>(Exception ex)
    {
        await Task.CompletedTask;
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private SwapsManagementService BuildService()
    {
        var spending = new SpendingService(
            vtxoStorage: _vtxoStorage,
            contractStorage: Substitute.For<IContractStorage>(),
            walletProvider: Substitute.For<IWalletProvider>(),
            coinService: Substitute.For<ICoinService>(),
            paymentService: Substitute.For<IContractService>(),
            transport: _transport,
            coinSelector: Substitute.For<ICoinSelector>(),
            safetyService: Substitute.For<ISafetyService>(),
            intentStorage: Substitute.For<IIntentStorage>());

        return new SwapsManagementService(
            providers: Array.Empty<ISwapProvider>(),
            spendingService: spending,
            clientTransport: _transport,
            vtxoStorage: _vtxoStorage,
            walletProvider: Substitute.For<IWalletProvider>(),
            swapsStorage: _swapStorage,
            contractService: Substitute.For<IContractService>(),
            contractStorage: Substitute.For<IContractStorage>(),
            safetyService: Substitute.For<ISafetyService>(),
            intentStorage: Substitute.For<IIntentStorage>(),
            chainTimeProvider: Substitute.For<IChainTimeProvider>());
    }
}

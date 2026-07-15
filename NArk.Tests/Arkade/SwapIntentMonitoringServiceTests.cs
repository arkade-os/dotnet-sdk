using NArk.Abstractions.VTXOs;
using NArk.Arkade.NonInteractiveSwaps;
using NArk.ArkadeIntents;
using NBitcoin;

namespace NArk.Tests.Arkade;

[TestFixture]
public class SwapIntentMonitoringServiceTests
{
    // ─── Pure status mapping ──────────────────────────────────────────

    [Test]
    public void ResolveTerminalStatus_Spent_IsFulfilled()
        => Assert.That(SwapIntentMonitoringService.ResolveTerminalStatus(Vtxo("s", spentBy: "tx")),
            Is.EqualTo(SwapIntentStatus.Fulfilled));

    [Test]
    public void ResolveTerminalStatus_Swept_IsRecoverable()
        => Assert.That(SwapIntentMonitoringService.ResolveTerminalStatus(Vtxo("s", swept: true)),
            Is.EqualTo(SwapIntentStatus.Recoverable));

    [Test]
    public void ResolveTerminalStatus_Open_IsNull()
        => Assert.That(SwapIntentMonitoringService.ResolveTerminalStatus(Vtxo("s")), Is.Null);

    // ─── Reactive → storage ───────────────────────────────────────────

    [Test]
    public async Task SpentVtxo_TransitionsStorageToFulfilled()
    {
        var (vtxos, intents, svc) = Build();
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", spentBy: "spendtx", arkTxid: "arktx"));

        Assert.That(intents.Updates, Has.Count.EqualTo(1));
        Assert.That(intents.Updates[0], Is.EqualTo(("script1", SwapIntentStatus.Fulfilled, "arktx")));
    }

    [Test]
    public async Task SweptVtxo_TransitionsStorageToRecoverable()
    {
        var (vtxos, intents, svc) = Build();
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", swept: true));

        Assert.That(intents.Updates[0], Is.EqualTo(("script1", SwapIntentStatus.Recoverable, (string?)null)));
    }

    [Test]
    public async Task OpenVtxo_DoesNotTouchStorage()
    {
        var (vtxos, intents, svc) = Build();
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1"));

        Assert.That(intents.Updates, Is.Empty);
    }

    [Test]
    public async Task StoppedMonitor_IgnoresChanges()
    {
        var (vtxos, intents, svc) = Build();
        await svc.StartAsync(default);
        await svc.StopAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", spentBy: "tx"));

        Assert.That(intents.Updates, Is.Empty);
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static (FakeVtxoStorage, FakeIntentStorage, SwapIntentMonitoringService) Build()
    {
        var vtxos = new FakeVtxoStorage();
        var intents = new FakeIntentStorage();
        return (vtxos, intents, new SwapIntentMonitoringService(vtxos, intents));
    }

    private static ArkVtxo Vtxo(string script, string? spentBy = null, bool swept = false, string? arkTxid = null) =>
        new(Script: script, TransactionId: "tx", TransactionOutputIndex: 0, Amount: 1000,
            SpentByTransactionId: spentBy, SettledByTransactionId: null, Swept: swept,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: arkTxid);

    private sealed class FakeVtxoStorage : IVtxoStorage
    {
        public event EventHandler<ArkVtxo>? VtxosChanged;
        public event EventHandler? ActiveScriptsChanged;

        public void RaiseVtxo(ArkVtxo vtxo) => VtxosChanged?.Invoke(this, vtxo);

        public Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(
            IReadOnlyCollection<string>? scripts = null,
            IReadOnlyCollection<OutPoint>? outpoints = null,
            string[]? walletIds = null,
            bool includeSpent = false,
            string? searchText = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ArkVtxo>>(Array.Empty<ArkVtxo>());
    }

    private sealed class FakeIntentStorage : IArkadeIntentStorage
    {
        public event EventHandler<SwapIntent>? SwapsChanged;
        public event EventHandler? ActiveScriptsChanged;

        public readonly List<(string Script, SwapIntentStatus Status, string? SpentTxid)> Updates = new();

        public Task<IReadOnlyCollection<SwapIntent>> GetSwapIntents(
            SwapIntentStatus? status = null,
            string? swapPkScript = null,
            string[]? walletIds = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SwapIntent>>(Array.Empty<SwapIntent>());

        public Task SaveSwapIntent(SwapIntent intent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> UpdateStatus(string swapPkScript, SwapIntentStatus status, string? spentTxid = null,
            CancellationToken cancellationToken = default)
        {
            Updates.Add((swapPkScript, status, spentTxid));
            _ = SwapsChanged; // referenced to avoid unused-event warning
            return Task.FromResult(true);
        }
    }
}

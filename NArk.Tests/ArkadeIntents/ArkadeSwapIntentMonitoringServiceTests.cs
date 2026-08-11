using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents;

[TestFixture]
public class ArkadeSwapIntentMonitoringServiceTests
{
    // ─── Pure status mapping ──────────────────────────────────────────

    [Test]
    public void Machine_Spent_IsFulfilled()
        => Assert.That(ArkadeSwapStateMachine.Next(ArkadeSwapIntentType.BtcToAsset, ArkadeSwapIntentStatus.Pending, SwapObservation.From(Vtxo("s", spentBy: "tx"), 0, null)),
            Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));

    [Test]
    public void Machine_Swept_IsRecoverable()
        => Assert.That(ArkadeSwapStateMachine.Next(ArkadeSwapIntentType.BtcToAsset, ArkadeSwapIntentStatus.Pending, SwapObservation.From(Vtxo("s", swept: true), 0, null)),
            Is.EqualTo(ArkadeSwapIntentStatus.Recoverable));

    [Test]
    public void Machine_Open_IsNull()
        => Assert.That(ArkadeSwapStateMachine.Next(ArkadeSwapIntentType.BtcToAsset, ArkadeSwapIntentStatus.Pending, SwapObservation.From(Vtxo("s"), 0, null)), Is.Null);

    // ─── Reactive → storage ───────────────────────────────────────────

    [Test]
    public async Task SpentVtxo_TransitionsStorageToFulfilled()
    {
        var (vtxos, intents, svc) = Build();
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", spentBy: "spendtx", arkTxid: "arktx"));

        Assert.That(intents.Updates, Has.Count.EqualTo(1));
        Assert.That(intents.Updates[0], Is.EqualTo(("script1", ArkadeSwapIntentStatus.Fulfilled, "arktx")));
    }

    [Test]
    public async Task SweptVtxo_TransitionsStorageToRecoverable()
    {
        var (vtxos, intents, svc) = Build();
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", swept: true));

        Assert.That(intents.Updates[0], Is.EqualTo(("script1", ArkadeSwapIntentStatus.Recoverable, (string?)null)));
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

    private static (FakeVtxoStorage, FakeIntentStorage, ArkadeSwapIntentMonitoringService) Build(
        ArkadeSwapIntentType type = ArkadeSwapIntentType.BtcToAsset, long? refundLocktime = null)
    {
        var vtxos = new FakeVtxoStorage();
        var intents = new FakeIntentStorage();
        intents.Swaps["script1"] = new ArkadeSwapIntent
        {
            Id = "swap-1",
            WalletId = "wallet-1",
            Type = type,
            OfferAmount = NBitcoin.Money.Satoshis(1000),
            WantAmount = NBitcoin.Money.Satoshis(1000),
            Status = ArkadeSwapIntentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            SwapPkScript = "script1",
            SwapAddress = "tark1example",
            OfferHex = "",
            RefundLocktime = refundLocktime,
        };
        return (vtxos, intents, new ArkadeSwapIntentMonitoringService(vtxos, intents));
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
        public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
        public event EventHandler? ActiveScriptsChanged;

        public readonly List<(string Script, ArkadeSwapIntentStatus Status, string? SpentTxid)> Updates = new();

        /// <summary>
        /// Swaps this store knows about, keyed by lockup script.
        /// </summary>
        /// <remarks>
        /// Seeded rather than empty on purpose. The monitor needs a swap's corridor and locktime to
        /// decide anything — without them a spend on a Lightning lockup would read as a fill even
        /// past the refund deadline, where it is genuinely ambiguous. A fake that returned nothing
        /// let the tests pass through a path the real system never takes.
        /// </remarks>
        public readonly Dictionary<string, ArkadeSwapIntent> Swaps = new();

        public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
            ArkadeSwapIntentStatus? status = null,
            string? swapPkScript = null,
            string[]? walletIds = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(
                swapPkScript is not null && Swaps.TryGetValue(swapPkScript, out var one)
                    ? [one]
                    : Swaps.Values.ToArray());

        public Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> UpdateStatus(string swapPkScript, ArkadeSwapIntentStatus status, string? spentTxid = null,
            CancellationToken cancellationToken = default)
        {
            Updates.Add((swapPkScript, status, spentTxid));
            _ = SwapsChanged; // referenced to avoid unused-event warning
            return Task.FromResult(true);
        }
    }
}

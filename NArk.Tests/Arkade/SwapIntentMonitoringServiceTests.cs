using NArk.Abstractions.VTXOs;
using NArk.Arkade.NonInteractiveSwaps;
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

    // ─── Reactive transitions ─────────────────────────────────────────

    [Test]
    public void SpentVtxo_MarksIntentFulfilled_AndUntracks()
    {
        var storage = new FakeVtxoStorage();
        using var svc = new SwapIntentMonitoringService(storage);
        var intent = Intent("script1");
        SwapIntent? updated = null;
        svc.IntentUpdated += (_, i) => updated = i;

        svc.Track(intent);
        storage.RaiseVtxo(Vtxo("script1", spentBy: "spendtx", arkTxid: "arktx"));

        Assert.That(intent.Status, Is.EqualTo(SwapIntentStatus.Fulfilled));
        Assert.That(intent.SpentTxid, Is.EqualTo("arktx")); // prefers ArkTxid over SpentBy
        Assert.That(updated, Is.SameAs(intent));
        Assert.That(svc.Pending, Is.Empty);
    }

    [Test]
    public void SweptVtxo_MarksIntentRecoverable()
    {
        var storage = new FakeVtxoStorage();
        using var svc = new SwapIntentMonitoringService(storage);
        var intent = Intent("script1");

        svc.Track(intent);
        storage.RaiseVtxo(Vtxo("script1", swept: true));

        Assert.That(intent.Status, Is.EqualTo(SwapIntentStatus.Recoverable));
        Assert.That(svc.Pending, Is.Empty);
    }

    [Test]
    public void OpenVtxo_LeavesIntentPending()
    {
        var storage = new FakeVtxoStorage();
        using var svc = new SwapIntentMonitoringService(storage);
        var intent = Intent("script1");

        svc.Track(intent);
        storage.RaiseVtxo(Vtxo("script1"));

        Assert.That(intent.Status, Is.EqualTo(SwapIntentStatus.Pending));
        Assert.That(svc.Pending, Has.Count.EqualTo(1));
    }

    [Test]
    public void UntrackedScript_Ignored()
    {
        var storage = new FakeVtxoStorage();
        using var svc = new SwapIntentMonitoringService(storage);
        var raised = false;
        svc.IntentUpdated += (_, _) => raised = true;

        storage.RaiseVtxo(Vtxo("some-other-script", spentBy: "tx"));

        Assert.That(raised, Is.False);
    }

    [Test]
    public void CancellingIntent_IsNotReadAsFulfillment()
    {
        var storage = new FakeVtxoStorage();
        using var svc = new SwapIntentMonitoringService(storage);
        var intent = Intent("script1");
        svc.Track(intent);

        // Caller moves it out of Pending before spending the cancel path.
        intent.Status = SwapIntentStatus.Cancelling;
        storage.RaiseVtxo(Vtxo("script1", spentBy: "canceltx"));

        Assert.That(intent.Status, Is.EqualTo(SwapIntentStatus.Cancelling));
    }

    [Test]
    public async Task GetActiveScripts_ReturnsPendingScripts()
    {
        var storage = new FakeVtxoStorage();
        using var svc = new SwapIntentMonitoringService(storage);
        svc.Track(Intent("script1"));
        svc.Track(Intent("script2"));

        var scripts = await svc.GetActiveScripts();

        Assert.That(scripts, Is.EquivalentTo(new[] { "script1", "script2" }));
    }

    [Test]
    public void Track_RaisesActiveScriptsChanged()
    {
        var storage = new FakeVtxoStorage();
        using var svc = new SwapIntentMonitoringService(storage);
        var raised = 0;
        svc.ActiveScriptsChanged += (_, _) => raised++;

        svc.Track(Intent("script1"));
        svc.Track(Intent("script1")); // duplicate → no-op
        svc.Untrack("script1");

        Assert.That(raised, Is.EqualTo(2)); // one add, one remove
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static SwapIntent Intent(string pkScript) => new()
    {
        Type = SwapIntentType.BtcToAsset,
        OfferAmount = Money.Satoshis(10_000),
        WantAmount = Money.Satoshis(500),
        Status = SwapIntentStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        SwapPkScript = pkScript,
    };

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
}

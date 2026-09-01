using NArk.Abstractions.Helpers;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

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

    // ─── Lightning: a spend is a fill only when the preimage proves it ──

    [Test]
    public async Task SpentLightningLockup_WithRevealedPreimage_IsFulfilled()
    {
        var (vtxos, intents, svc) = Build(
            ArkadeSwapIntentType.BtcToLightning,
            transport: TransportReturning(SpendOf(LockupOutpoint, Preimage)));
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", spentBy: "spendtx", arkTxid: "arktx"));

        Assert.That(intents.Updates, Has.Count.EqualTo(1));
        Assert.That(intents.Updates[0], Is.EqualTo(("script1", ArkadeSwapIntentStatus.Fulfilled, "arktx")));
    }

    [Test]
    public async Task SpentLightningLockup_WithoutAPreimage_IsResolvedNotFulfilled()
    {
        // The covenant's non-interactive refund carries no timelock and no preimage, so a bare
        // spend says the script moved, not that the invoice was paid.
        var (vtxos, intents, svc) = Build(
            ArkadeSwapIntentType.BtcToLightning,
            transport: TransportReturning(SpendOf(LockupOutpoint)));
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", spentBy: "spendtx"));

        Assert.That(intents.Updates, Has.Count.EqualTo(1));
        Assert.That(intents.Updates[0], Is.EqualTo(("script1", ArkadeSwapIntentStatus.Resolved, "spendtx")));
    }

    [Test]
    public async Task SpentLightningLockup_WhenTheIndexerIsDown_IsResolvedButRecorded()
    {
        // A read failure is "no proof", not a crash: the transition still lands, and a later
        // reconcile upgrades it once the spending transaction is fetchable.
        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<string>>>(_ => throw new HttpRequestException("indexer down"));
        var (vtxos, intents, svc) = Build(ArkadeSwapIntentType.BtcToLightning, transport: transport);
        await svc.StartAsync(default);

        vtxos.RaiseVtxo(Vtxo("script1", spentBy: "spendtx"));

        Assert.That(intents.Updates, Has.Count.EqualTo(1));
        Assert.That(intents.Updates[0].Status, Is.EqualTo(ArkadeSwapIntentStatus.Resolved));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static readonly byte[] Preimage =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

    private static readonly OutPoint LockupOutpoint = new(uint256.One, 0);

    private static string PaymentHash =>
        Convert.ToHexString(NBitcoin.Crypto.Hashes.SHA256(Preimage)).ToLowerInvariant();

    /// <summary>A PSBT spending the lockup, optionally revealing the preimage in its condition field.</summary>
    private static string SpendOf(OutPoint prevOut, byte[]? preimage = null)
    {
        var tx = Network.Main.CreateTransaction();
        tx.Inputs.Add(new TxIn(prevOut));
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));

        var psbt = PSBT.FromTransaction(tx, Network.Main);
        if (preimage is not null) psbt.Inputs[0].SetArkFieldConditionWitness(new WitScript(Op.GetPushOp(preimage)));
        return psbt.ToBase64();
    }

    private static IClientTransport TransportReturning(params string[] psbts)
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(psbts));
        return transport;
    }

    private static (FakeVtxoStorage, FakeIntentStorage, ArkadeSwapIntentMonitoringService) Build(
        ArkadeSwapIntentType type = ArkadeSwapIntentType.BtcToAsset,
        long? refundLocktime = null,
        IClientTransport? transport = null)
    {
        var vtxos = new FakeVtxoStorage();
        var intents = new FakeIntentStorage();
        var isLightning = type is ArkadeSwapIntentType.BtcToLightning or ArkadeSwapIntentType.LightningToBtc;
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
            RefundLocktime = refundLocktime,
            PaymentHash = isLightning ? PaymentHash : null,
        };
        return (vtxos, intents, new ArkadeSwapIntentMonitoringService(
            vtxos, intents, transport ?? TransportReturning()));
    }

    private static ArkVtxo Vtxo(string script, string? spentBy = null, bool swept = false, string? arkTxid = null) =>
        new(Script: script, TransactionId: LockupOutpoint.Hash.ToString(), TransactionOutputIndex: 0, Amount: 1000,
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
            string? id = null,
            ArkadeSwapIntentStatus? status = null,
            string? swapPkScript = null,
            string[]? walletIds = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
        {
            // Honours every filter it is given: a fake that answers a lookup by id with the whole
            // table would let a caller move the wrong swap's money and still pass.
            IEnumerable<ArkadeSwapIntent> q =
                swapPkScript is not null && Swaps.TryGetValue(swapPkScript, out var one)
                    ? [one]
                    : Swaps.Values;
            if (id is not null) q = q.Where(i => i.Id == id);
            if (status is { } st) q = q.Where(i => i.Status == st);
            return Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(q.ToArray());
        }

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

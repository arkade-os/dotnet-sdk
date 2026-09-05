using NArk.Abstractions;
using NArk.Abstractions.Helpers;
using NSubstitute;
using NArk.Core.Transport;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// Catching up on everything that happened while nobody was watching.
/// </summary>
/// <remarks>
/// The monitor is event-driven, so a process that was down missed whatever the chain did in the
/// meantime — and on the receive leg a claim window can open and close inside that gap. Without a
/// pass like this, a restart resumes from a picture that stopped being true when the process did,
/// and the sweep that follows acts on it.
/// </remarks>
[TestFixture]
public class ArkadeIntentsReconciliationTests
{
    private const long Locktime = 1_800_000_000;

    [Test]
    public async Task AFundingSwapWhoseLockupAppeared_IsPromoted()
    {
        // The swap was recorded before its own spend. Seeing the lockup is the only confirmation it
        // ever gets that the money actually moved.
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Funding),
            Vtxo());

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated.Single().To, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
            Assert.That(storage.Saved.Single().Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
            Assert.That(result.FundingUnconfirmed, Is.Empty);
        });
    }

    [Test]
    public async Task AFundingSwapWithNoLockup_IsReportedAndLeftAlone()
    {
        // Still in flight or never landed — nothing on our side tells those apart, and guessing
        // either way abandons a live swap or resurrects a dead one.
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Funding),
            vtxo: null);

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.FundingUnconfirmed, Is.EqualTo(new[] { "swap-1" }));
            Assert.That(result.Updated, Is.Empty);
            Assert.That(storage.Saved, Is.Empty, "nothing was written");
        });
    }

    [Test]
    public async Task AReceiveSwapFundedWhileWeWereDown_BecomesClaimable()
    {
        // The gap this exists for: the solver funded, the monitor was not running, and the claim
        // window is finite.
        var (service, _) = Build(
            Intent(ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Pending),
            Vtxo());

        var result = await service.ReconcileAsync();

        Assert.That(result.Updated.Single().To, Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
    }

    [Test]
    public async Task ItLooksAtSpentOutputs()
    {
        // The default chain view hides them, and a lockup the counterparty already took is the one
        // outcome this pass exists to notice.
        var (service, _) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Pending), Vtxo());

        await service.ReconcileAsync();

        Assert.That(_lastVtxos.AskedForSpent, Is.True);
    }

    [Test]
    public async Task ASpentLightningLockup_IsNotAssumedFilled()
    {
        // The spend is recorded, but nothing here proves who moved it: the counterparty can push the
        // covenant's untimelocked refund at any time. Reading this as a fill would report a refunded
        // payment as a completed one — an order settled against money that came back.
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Pending),
            Vtxo(spentBy: "spendtx", arkTxid: "arktx"));

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated.Single().To, Is.EqualTo(ArkadeSwapIntentStatus.Resolved));
            Assert.That(storage.Saved.Single().SpentTxid, Is.EqualTo("arktx"));
        });
    }

    [Test]
    public async Task ATerminalSwap_IsNotReExamined()
    {
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Fulfilled),
            Vtxo(spentBy: "tx"));

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.Empty);
            Assert.That(storage.Saved, Is.Empty);
        });
    }

    [Test]
    public async Task ASpentLightningLockup_WithAProvenPreimage_IsFulfilled()
    {
        // The proof the monitor was wired for, through the reconciliation path.
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Pending, withPaymentHash: true),
            Vtxo(spentBy: "spendtx", arkTxid: "arktx"),
            TransportReturning(SpendOf(LockupOutpoint, Preimage)));

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated.Single().To, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
            Assert.That(storage.Saved.Single().Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        });
    }

    [Test]
    public async Task AResolvedSwap_WithAProvenPreimage_IsUpgradedToFulfilled()
    {
        // Resolved is terminal for everything except this: it may have been recorded on a
        // transient indexer miss, and the preimage readable now proves it was a fill all along.
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Resolved, withPaymentHash: true),
            Vtxo(spentBy: "spendtx", arkTxid: "arktx"),
            TransportReturning(SpendOf(LockupOutpoint, Preimage)));

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated.Single(), Is.EqualTo(
                new ArkadeIntentReconciled("swap-1", ArkadeSwapIntentStatus.Resolved, ArkadeSwapIntentStatus.Fulfilled)));
            Assert.That(storage.Saved.Single().Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        });
    }

    [Test]
    public async Task AResolvedSwap_WithNoProofReadable_StaysResolved()
    {
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Resolved, withPaymentHash: true),
            Vtxo(spentBy: "spendtx"));

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.Empty);
            Assert.That(storage.Saved, Is.Empty);
        });
    }

    [Test]
    public async Task AReceiveSwapPastItsClaimWindow_IsResolvedOnTheAdvancePass()
    {
        // Deadlines raise no chain event, so only the clock can close this: without it the swap
        // sits Claimable forever and every pass retries a claim that throws.
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Claimable),
            Vtxo(),
            clock: new FakeClock(Locktime + 3600));

        await service.AdvanceAllAsync();

        Assert.That(storage.Saved.Single().Status, Is.EqualTo(ArkadeSwapIntentStatus.Resolved));
    }

    [Test]
    public async Task ASwapAlreadyInTheRightState_IsNotRewritten()
    {
        // Reconciliation is meant to be run on every startup, so a no-op pass must actually be one.
        var (service, storage) = Build(
            Intent(ArkadeSwapIntentType.BtcToLightning, ArkadeSwapIntentStatus.Pending),
            Vtxo());

        var result = await service.ReconcileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.Empty);
            Assert.That(storage.Saved, Is.Empty);
        });
    }

    // ─── Harness ──────────────────────────────────────────────────────

    private static FakeVtxos _lastVtxos = new(null);

    private static readonly byte[] Preimage =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

    private static readonly OutPoint LockupOutpoint = new(new uint256(new string('b', 64)), 0);

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

    /// <summary>
    /// A transport that yields no transactions, so nothing can be proven from a spend.
    /// </summary>
    /// <remarks>
    /// Deliberately silent rather than fabricating a witness: what these tests pin is that an
    /// unproven spend is never read as a fill.
    /// </remarks>
    private static IClientTransport SilentTransport() => TransportReturning();

    private static (ArkadeIntentsService, FakeIntents) Build(
        ArkadeSwapIntent intent, ArkVtxo? vtxo, IClientTransport? transport = null, FakeClock? clock = null)
    {
        var intents = new FakeIntents(intent);
        var vtxos = _lastVtxos = new FakeVtxos(vtxo);

        // Only the storages and the clock take part in reconciliation; the corridor clients are
        // never reached, so they are left null rather than mocked into existence.
        // Named throughout: this fixture passes nulls for what reconciliation never touches, and
        // positionally that makes every future parameter a silent shift.
        return (new ArkadeIntentsService(
            assets: null!,
            lightning: null!,
            intentStorage: intents,
            vtxoStorage: vtxos,
            transport: transport ?? SilentTransport(),
            time: clock ?? new FakeClock(Locktime - 3600)), intents);
    }

    private static ArkadeSwapIntent Intent(
        ArkadeSwapIntentType type, ArkadeSwapIntentStatus status, bool withPaymentHash = false) => new()
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
        RefundLocktime = Locktime,
        PaymentHash = withPaymentHash
            ? Convert.ToHexString(NBitcoin.Crypto.Hashes.SHA256(Preimage)).ToLowerInvariant()
            : null,
    };

    private static ArkVtxo Vtxo(string? spentBy = null, string? arkTxid = null) =>
        new(Script: "5120" + new string('a', 64), TransactionId: new string('b', 64),
            TransactionOutputIndex: 0, Amount: 50_000,
            SpentByTransactionId: spentBy, SettledByTransactionId: null, Swept: false,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: arkTxid);

    private sealed class FakeClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    private sealed class FakeIntents(ArkadeSwapIntent intent) : IArkadeIntentStorage
    {
        public readonly List<ArkadeSwapIntent> Saved = [];
        public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
        public event EventHandler? ActiveScriptsChanged;

        public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
            string? id = null,
            ArkadeSwapIntentStatus? status = null, ArkadeSwapIntentStatus[]? statuses = null,
            string? swapPkScript = null, string[]? walletIds = null,
            int? skip = null, int? take = null, CancellationToken cancellationToken = default)
        {
            // Applies the filters rather than always answering with the one intent. A fake that
            // ignores them lets a caller look up the wrong swap and still go green.
            IEnumerable<ArkadeSwapIntent> q = [intent];
            if (id is not null) q = q.Where(i => i.Id == id);
            if (status is { } s) q = q.Where(i => i.Status == s);
            if (statuses is { Length: > 0 }) q = q.Where(i => statuses.Contains(i.Status));
            if (swapPkScript is not null) q = q.Where(i => i.SwapPkScript == swapPkScript);
            return Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(q.ToList());
        }

        public Task SaveArkadeSwapIntent(ArkadeSwapIntent i, CancellationToken cancellationToken = default)
        {
            Saved.Add(i);
            SwapsChanged?.Invoke(this, i);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateStatus(
            string swapPkScript, ArkadeSwapIntentStatus status, string? spentTxid = null,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeVtxos(ArkVtxo? vtxo) : IVtxoStorage
    {
        public event EventHandler<ArkVtxo>? VtxosChanged;
        public event EventHandler? ActiveScriptsChanged;

        /// <summary>Records what the caller asked for, so the query itself can be asserted.</summary>
        public bool AskedForSpent;

        public Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(
            IReadOnlyCollection<string>? scripts = null,
            IReadOnlyCollection<NBitcoin.OutPoint>? outpoints = null,
            string[]? walletIds = null,
            bool includeSpent = false,
            string? searchText = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
        {
            AskedForSpent = includeSpent;
            return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(vtxo is null ? [] : [vtxo]);
        }

        public Task<bool> UpsertVtxo(ArkVtxo v, CancellationToken cancellationToken = default)
        {
            VtxosChanged?.Invoke(this, v);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }
    }
}

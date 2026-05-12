using NArk.Abstractions.Exit;
using NArk.Abstractions.VirtualTxs;
using NArk.Core.Exit;
using NArk.Core.VirtualTxs;
using NBitcoin;

namespace NArk.Tests.Exit;

[TestFixture]
public class InMemoryExitStorageTests
{
    private static OutPoint Outpoint(uint vout = 0) => new(RandomUtils.GetUInt256(), vout);

    private static ExitSession SessionFor(OutPoint op, ExitSessionState state = ExitSessionState.Broadcasting, string walletId = "w1") =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            VtxoTxid: op.Hash.ToString(),
            VtxoVout: op.N,
            WalletId: walletId,
            ClaimAddress: "bcrt1qexample",
            State: state,
            NextTxIndex: 0,
            ClaimTxid: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            FailReason: null);

    // ── InMemoryExitSessionStorage ─────────────────────────────────

    [Test]
    public async Task SessionStorage_Upsert_ThenLookupByIdAndVtxo()
    {
        var storage = new InMemoryExitSessionStorage();
        var op = Outpoint();
        var session = SessionFor(op);

        await storage.UpsertAsync(session);

        Assert.That(await storage.GetByIdAsync(session.Id), Is.EqualTo(session));
        Assert.That(await storage.GetByVtxoAsync(op), Is.EqualTo(session));
    }

    [Test]
    public async Task SessionStorage_Upsert_OverwritesAndKeepsVtxoIndex()
    {
        var storage = new InMemoryExitSessionStorage();
        var op = Outpoint();
        var session = SessionFor(op);
        await storage.UpsertAsync(session);

        var updated = session with { State = ExitSessionState.AwaitingCsvDelay, NextTxIndex = 3 };
        await storage.UpsertAsync(updated);

        var lookedUp = await storage.GetByVtxoAsync(op);
        Assert.That(lookedUp, Is.EqualTo(updated));
        Assert.That(lookedUp!.State, Is.EqualTo(ExitSessionState.AwaitingCsvDelay));
    }

    [Test]
    public async Task SessionStorage_GetByState_FiltersCorrectly()
    {
        var storage = new InMemoryExitSessionStorage();
        await storage.UpsertAsync(SessionFor(Outpoint(0), ExitSessionState.Broadcasting));
        await storage.UpsertAsync(SessionFor(Outpoint(1), ExitSessionState.AwaitingCsvDelay));
        await storage.UpsertAsync(SessionFor(Outpoint(2), ExitSessionState.Broadcasting));

        var broadcasting = await storage.GetByStateAsync(ExitSessionState.Broadcasting);
        Assert.That(broadcasting.Count, Is.EqualTo(2));
        Assert.That(broadcasting.All(s => s.State == ExitSessionState.Broadcasting));
    }

    [Test]
    public async Task SessionStorage_GetActive_ExcludesTerminalStatesAndFiltersByWallet()
    {
        var storage = new InMemoryExitSessionStorage();
        await storage.UpsertAsync(SessionFor(Outpoint(0), ExitSessionState.Broadcasting, "w1"));
        await storage.UpsertAsync(SessionFor(Outpoint(1), ExitSessionState.Completed, "w1"));
        await storage.UpsertAsync(SessionFor(Outpoint(2), ExitSessionState.Failed, "w1"));
        await storage.UpsertAsync(SessionFor(Outpoint(3), ExitSessionState.Claimable, "w2"));

        var active = await storage.GetActiveSessionsAsync();
        Assert.That(active.Count, Is.EqualTo(2),
            "Completed + Failed should be excluded from Active");

        var w1Active = await storage.GetActiveSessionsAsync("w1");
        Assert.That(w1Active.Count, Is.EqualTo(1));
        Assert.That(w1Active[0].WalletId, Is.EqualTo("w1"));
    }

    // ── InMemoryVirtualTxStorage ───────────────────────────────────

    [Test]
    public async Task VirtualTxStorage_SetBranch_ThenGetReturnsOrdered()
    {
        var storage = new InMemoryVirtualTxStorage();
        var op = Outpoint();

        var txA = new VirtualTx("txA", "hexA", null, ChainedTxType.Commitment);
        var txB = new VirtualTx("txB", "hexB", null, ChainedTxType.Tree);
        var txC = new VirtualTx("txC", "hexC", null, ChainedTxType.Ark);
        await storage.UpsertVirtualTxsAsync([txA, txB, txC]);

        await storage.SetBranchAsync(op,
        [
            new VtxoBranch(op.Hash.ToString(), op.N, "txC", 2),
            new VtxoBranch(op.Hash.ToString(), op.N, "txA", 0),
            new VtxoBranch(op.Hash.ToString(), op.N, "txB", 1),
        ]);

        var branch = await storage.GetBranchAsync(op);
        Assert.That(branch.Count, Is.EqualTo(3));
        Assert.That(branch.Select(t => t.Txid), Is.EqualTo(new[] { "txA", "txB", "txC" }));
    }

    [Test]
    public async Task VirtualTxStorage_Upsert_MergesNonNullFields()
    {
        var storage = new InMemoryVirtualTxStorage();

        // First write: Lite shape (no hex)
        await storage.UpsertVirtualTxsAsync([
            new VirtualTx("tx1", null, DateTimeOffset.UtcNow.AddHours(1), ChainedTxType.Tree)]);

        // Second write: Full shape (hex provided, expiry omitted)
        await storage.UpsertVirtualTxsAsync([
            new VirtualTx("tx1", "hexFull", null, ChainedTxType.Unspecified)]);

        var merged = await storage.GetVirtualTxAsync("tx1");
        Assert.That(merged, Is.Not.Null);
        Assert.That(merged!.Hex, Is.EqualTo("hexFull"), "Hex should be filled in by second write");
        Assert.That(merged.ExpiresAt, Is.Not.Null, "ExpiresAt should not be wiped by the second null write");
        Assert.That(merged.Type, Is.EqualTo(ChainedTxType.Tree), "Type should not be overwritten by Unspecified");
    }

    [Test]
    public async Task VirtualTxStorage_HasBranch_ReflectsState()
    {
        var storage = new InMemoryVirtualTxStorage();
        var op = Outpoint();

        Assert.That(await storage.HasBranchAsync(op), Is.False);

        await storage.UpsertVirtualTxsAsync([new VirtualTx("tx1", null, null)]);
        await storage.SetBranchAsync(op,
            [new VtxoBranch(op.Hash.ToString(), op.N, "tx1", 0)]);

        Assert.That(await storage.HasBranchAsync(op), Is.True);
    }

    [Test]
    public async Task VirtualTxStorage_Prune_RemovesBranchAndOrphans_KeepsSharedNodes()
    {
        var storage = new InMemoryVirtualTxStorage();
        var sharedRoot = new VirtualTx("shared", "hexShared", null, ChainedTxType.Tree);
        var leafA = new VirtualTx("leafA", "hexA", null, ChainedTxType.Ark);
        var leafB = new VirtualTx("leafB", "hexB", null, ChainedTxType.Ark);
        await storage.UpsertVirtualTxsAsync([sharedRoot, leafA, leafB]);

        var opA = Outpoint(0);
        var opB = Outpoint(1);
        await storage.SetBranchAsync(opA,
        [
            new VtxoBranch(opA.Hash.ToString(), opA.N, "shared", 0),
            new VtxoBranch(opA.Hash.ToString(), opA.N, "leafA", 1),
        ]);
        await storage.SetBranchAsync(opB,
        [
            new VtxoBranch(opB.Hash.ToString(), opB.N, "shared", 0),
            new VtxoBranch(opB.Hash.ToString(), opB.N, "leafB", 1),
        ]);

        // Prune A → leafA should be gone, shared should stay (still
        // referenced by B), leafB untouched.
        await storage.PruneForSpentVtxoAsync(opA);

        Assert.That(await storage.HasBranchAsync(opA), Is.False);
        Assert.That(await storage.HasBranchAsync(opB), Is.True);
        Assert.That(await storage.GetVirtualTxAsync("leafA"), Is.Null, "Orphan leafA should be removed");
        Assert.That(await storage.GetVirtualTxAsync("shared"), Is.Not.Null,
            "Shared root should remain — still referenced by B's branch");
        Assert.That(await storage.GetVirtualTxAsync("leafB"), Is.Not.Null);
    }
}

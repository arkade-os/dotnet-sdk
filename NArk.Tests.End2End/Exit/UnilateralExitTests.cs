using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Exit;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Storage.EfCore.Storage;
using NArk.Tests.Common;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport.GrpcClient;
using NBitcoin;
using NBXplorer;

namespace NArk.Tests.End2End.Core;

/// <summary>
/// End-to-end coverage for the unilateral exit pipeline (PR #39).
///
/// Mirrors the equivalent suites in arkade-os/go-sdk
/// (TestUnilateralExit/{leaf vtxo,preconfirmed vtxo}) and arkade-os/ts-sdk
/// (should unroll / should reject complete-unroll before unilateral exit
/// delay matures / should complete unroll after unilateral exit delay).
///
/// Setup (per-test):
///   1. Derive a boarding contract for a fresh wallet
///   2. Faucet the boarding address via bitcoin-cli sendtoaddress
///   3. Mine 6 blocks to confirm the boarding UTXO
///   4. Sync via Esplora, then run intent generation + batch settlement
///      so the wallet ends up holding a *settled* VTXO whose ancestry chain
///      anchors at a real on-chain commitment tx — the only kind of VTXO
///      that can actually be exited unilaterally.
///   5. Start the exit on that VTXO and assert state-machine transitions.
///
/// The full broadcast → confirm → CSV → claim path requires advancing the
/// chain past UnilateralExit blocks. We exercise that explicitly via
/// DockerHelper.MineBlocks and ProgressExitsAsync polling.
/// </summary>
public class UnilateralExitTests
{
    private const int BoardingAmountSats = 100_000;

    /// <summary>
    /// Smoke test: after a successful batch settle, StartExitAsync creates an
    /// ExitSession in Broadcasting state with the virtual tx branch populated.
    /// </summary>
    [Test]
    public async Task CanStartUnilateralExitForSettledVtxo()
    {
        await using var setup = await SettleAVtxoAsync();

        // Pick the unspent post-settle VTXO (boarding UTXO is now spent).
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.FirstOrDefault(v => !v.IsSpent() && !v.Unrolled);
        Assert.That(vtxo, Is.Not.Null,
            "Expected an unspent settled VTXO after batch round; got: " +
            string.Join(", ", vtxos.Select(v => $"{v.TransactionId[..8]}..:{v.TransactionOutputIndex} spent={v.IsSpent()} unrolled={v.Unrolled}")));

        var claimAddress = await GetFreshOnchainAddress();

        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId,
            [vtxo!.OutPoint],
            claimAddress,
            CancellationToken.None);

        Assert.That(sessions, Has.Count.EqualTo(1));
        var session = sessions[0];
        Assert.That(session.State, Is.EqualTo(ExitSessionState.Broadcasting));
        Assert.That(session.NextTxIndex, Is.EqualTo(0));
        Assert.That(session.WalletId, Is.EqualTo(setup.WalletId));
        Assert.That(session.ClaimAddress, Is.EqualTo(claimAddress.ToString()));

        // Branch must be populated as part of StartExit (EnsureHexPopulatedAsync).
        // The whole chain (including the on-chain Commitment anchor) is
        // stored — Commitment rows are intentionally hex-null since arkd's
        // GetVirtualTxs only carries hex for off-chain rows.
        var branch = await setup.VirtualTxStorage.GetBranchAsync(vtxo.OutPoint);
        Assert.That(branch, Has.Count.GreaterThan(0),
            "Virtual tx branch should be fetched during StartExitAsync");
        Assert.That(branch.Any(tx => tx.Type == ChainedTxType.Commitment), Is.True,
            "Branch should include the on-chain Commitment row (whole-chain storage)");
        Assert.That(branch.Where(tx => tx.Type != ChainedTxType.Commitment)
                          .All(tx => tx.Hex is not null), Is.True,
            "All non-Commitment virtual txs should have hex populated (Full mode)");
    }

    /// <summary>
    /// Calling StartExitAsync twice for the same VTXO returns the existing
    /// session rather than creating a duplicate.
    /// </summary>
    [Test]
    public async Task StartExit_IsIdempotentForSameVtxo()
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.First(v => !v.IsSpent() && !v.Unrolled);
        var claimAddress = await GetFreshOnchainAddress();

        var firstCall = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, CancellationToken.None);
        var secondCall = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, CancellationToken.None);

        Assert.That(firstCall, Has.Count.EqualTo(1));
        Assert.That(secondCall, Has.Count.EqualTo(1));
        Assert.That(secondCall[0].Id, Is.EqualTo(firstCall[0].Id),
            "StartExit should return the existing session, not create a duplicate");
    }

    /// <summary>
    /// Equivalent of go-sdk TestUnilateralExit/leaf vtxo and ts-sdk
    /// "should unroll": iteratively progress the exit, mining a block per
    /// step, and assert that the session advances Broadcasting →
    /// AwaitingCsvDelay (every virtual tx confirmed) within a reasonable
    /// budget.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task ProgressExits_AdvancesFromBroadcastingToAwaitingCsvDelay(CancellationToken token)
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.First(v => !v.IsSpent() && !v.Unrolled);
        var claimAddress = await GetFreshOnchainAddress();

        await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, token);

        var branch = await setup.VirtualTxStorage.GetBranchAsync(vtxo.OutPoint, token);
        TestContext.WriteLine($"[Exit] chain depth={branch.Count} (incl. commitment)");

        // Drive the state machine. Each iteration: progress (broadcasts what
        // it can), mine 1 block to confirm what's in mempool, observe state.
        // Use GetByVtxoAsync (not GetActiveSessionsAsync) so a Failed
        // session is still surfaced — otherwise we'd silently lose it.
        //
        // No fixed step cap: since the fix for TRUC/v3 (BIP 431), broadcasting
        // advances exactly one chain link per ProgressExitsAsync call (broadcast,
        // then wait for confirmation before touching the next link — see
        // UnilateralExitService.ProgressBroadcastingAsync), so a deeper chain
        // — e.g. when CI batches multiple participants into the same round —
        // legitimately needs more iterations than a single-hop local run. The
        // [CancelAfter] attribute is the real backstop against a genuine hang.
        ExitSession? current = null;
        for (var step = 0; !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);

            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current is null) continue;

            TestContext.WriteLine(
                $"[Exit] step={step} state={current.State} nextTxIndex={current.NextTxIndex} " +
                $"retry={current.RetryCount} fail={current.FailReason ?? "-"}");

            if (current.State == ExitSessionState.AwaitingCsvDelay
                || current.State == ExitSessionState.Claimable
                || current.State == ExitSessionState.Claiming
                || current.State == ExitSessionState.Completed)
            {
                break;
            }

            if (current.State == ExitSessionState.Failed)
            {
                Assert.Fail($"Exit session unexpectedly failed: {current.FailReason}");
            }
        }

        Assert.That(current, Is.Not.Null);
        Assert.That(current!.State,
            Is.EqualTo(ExitSessionState.AwaitingCsvDelay)
                .Or.EqualTo(ExitSessionState.Claimable)
                .Or.EqualTo(ExitSessionState.Claiming)
                .Or.EqualTo(ExitSessionState.Completed),
            $"Exit should advance past Broadcasting; final state={current.State}, " +
            $"nextTxIndex={current.NextTxIndex}, retries={current.RetryCount}, " +
            $"fail={current.FailReason ?? "-"}");
    }

    /// <summary>
    /// Equivalent of ts-sdk "should reject complete-unroll before unilateral
    /// exit delay matures": once a session reaches AwaitingCsvDelay, calling
    /// ProgressExitsAsync repeatedly without advancing the chain past the
    /// CSV must NOT promote the session to Claimable. Mining the full
    /// CSV-equivalent block range then promotes it.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public async Task AwaitingCsvDelay_DoesNotAdvanceUntilDelayMatures(CancellationToken token)
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.First(v => !v.IsSpent() && !v.Unrolled);
        var claimAddress = await GetFreshOnchainAddress();

        await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, token);

        var branch = await setup.VirtualTxStorage.GetBranchAsync(vtxo.OutPoint, token);
        TestContext.WriteLine($"[Exit] chain depth={branch.Count} (incl. commitment)");

        // 1. Drive to AwaitingCsvDelay (broadcast + 1-block confirms). No fixed
        // step cap — see the comment in ProgressExits_AdvancesFromBroadcastingToAwaitingCsvDelay
        // for why a deeper (CI-only) chain legitimately needs more iterations.
        ExitSession? current = null;
        for (var step = 0; !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current?.State is ExitSessionState.AwaitingCsvDelay
                or ExitSessionState.Claimable
                or ExitSessionState.Claiming
                or ExitSessionState.Completed) break;
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Exit failed: {current.FailReason}");
        }

        Assert.That(current?.State, Is.EqualTo(ExitSessionState.AwaitingCsvDelay)
            .Or.EqualTo(ExitSessionState.Claimable)
            .Or.EqualTo(ExitSessionState.Claiming)
            .Or.EqualTo(ExitSessionState.Completed));

        // If we're already past AwaitingCsvDelay (very short CSV in this
        // regtest config), nothing to assert about the rejection — skip.
        if (current!.State != ExitSessionState.AwaitingCsvDelay) return;

        // 2. Without mining further, ProgressExitsAsync several times and
        //    assert state stays at AwaitingCsvDelay (the leaf is confirmed
        //    but the CSV countdown hasn't advanced enough).
        for (var probe = 0; probe < 5; probe++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            Assert.That(current?.State, Is.EqualTo(ExitSessionState.AwaitingCsvDelay),
                $"Session should not advance to Claimable before CSV delay matures " +
                $"(probe {probe}, observed state={current?.State})");
        }

        // 3. Mine enough blocks to satisfy the CSV delay, then progress.
        // arkd v0.9 returns unilateral_exit_delay as an NBitcoin Sequence —
        // it can be block-based (LockType=Height) OR time-based (LockType=Time,
        // 512s units, BIP68 bit 22 set). Casting `.Value` to int in the
        // time-based case produces an enormous number (e.g. 24h ≈ 4194474);
        // mining that many regtest blocks degrades bitcoind/LND/Boltz for the
        // rest of the test run, masking real failures elsewhere. So gate on
        // LockType.
        var serverInfo = await setup.ClientTransport.GetServerInfoAsync(token);
        var unilateralExit = serverInfo.UnilateralExit;
        if (unilateralExit.LockType != SequenceLockType.Height)
        {
            // Time-based CSV in regtest can only be matured via setmocktime
            // (block timestamps + MTP), which arkd's CSV check itself doesn't
            // currently reason about (it compares chainTime.Height to a raw
            // encoded Sequence). Track that as separate work; for now exit
            // after validating the don't-advance-early half.
            TestContext.WriteLine(
                $"[Exit] CSV delay is time-based (LockPeriod={unilateralExit.LockPeriod}); " +
                "skipping post-mature assertion until time-based CSV maturation is wired up.");
            return;
        }

        var csvBlocks = unilateralExit.LockHeight + 2;
        TestContext.WriteLine($"[Exit] mining {csvBlocks} blocks to mature CSV delay");
        await DockerHelper.MineBlocks(csvBlocks, token);

        for (var step = 0; step < 10 && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current?.State is not ExitSessionState.AwaitingCsvDelay) break;
            await Task.Delay(500, token);
        }

        Assert.That(current?.State,
            Is.EqualTo(ExitSessionState.Claimable)
                .Or.EqualTo(ExitSessionState.Claiming)
                .Or.EqualTo(ExitSessionState.Completed),
            $"After mining past CSV delay, session should advance from AwaitingCsvDelay; " +
            $"observed state={current?.State}, fail={current?.FailReason ?? "-"}");
    }

    /// <summary>
    /// Equivalent of go-sdk TestUnilateralExit end-to-end and ts-sdk "should
    /// complete unroll after unilateral exit delay": drive the whole pipeline
    /// past the CSV delay, claim on-chain, and assert the session reaches
    /// <see cref="ExitSessionState.Completed"/> AND the claimed funds actually
    /// land at the on-chain claim address.
    ///
    /// This is the piece the other exit suites intentionally stop short of —
    /// they only assert the session *reaches* Claimable/Claiming/Completed as
    /// an alternation. Here we require Completed specifically and verify the
    /// money moved, which is only reliably assertable now that the regtest
    /// config is block-based (a block-height CSV delay matures by mining;
    /// the previous time-based config could not be matured in a way arkd's
    /// CSV check honoured — see AwaitingCsvDelay_DoesNotAdvanceUntilDelayMatures).
    /// </summary>
    [Test]
    [CancelAfter(300_000)]
    public async Task FullExit_ReachesCompleted_AndFundsLandAtClaimAddress(CancellationToken token)
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.First(v => !v.IsSpent() && !v.Unrolled);
        var vtxoAmount = Money.Satoshis(vtxo.Amount);

        // Use a bitcoin-core wallet address so getreceivedbyaddress can track
        // the claimed funds — a freshly-derived external taproot address is
        // unknown to the node's wallet and would always report zero.
        var claimAddress = await GetFreshOnchainAddress();

        await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, token);

        var branch = await setup.VirtualTxStorage.GetBranchAsync(vtxo.OutPoint, token);
        TestContext.WriteLine($"[Exit] chain depth={branch.Count} (incl. commitment), amount={vtxoAmount}");

        // Phase 1: broadcast the tree root-to-leaf until every link confirms
        // (Broadcasting → AwaitingCsvDelay). One link per ProgressExitsAsync
        // call (TRUC/v3 one-at-a-time broadcast), so no fixed step cap — the
        // [CancelAfter] budget is the real backstop.
        ExitSession? current = null;
        for (var step = 0; !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Exit failed during broadcast: {current.FailReason}");
            if (current?.State is ExitSessionState.AwaitingCsvDelay
                or ExitSessionState.Claimable or ExitSessionState.Claiming
                or ExitSessionState.Completed) break;
        }
        Assert.That(current, Is.Not.Null);

        // arkd v0.9 may return a time-based unilateral-exit delay. A block-based
        // regtest config yields LockType=Height, which we mature by mining; a
        // time-based delay can't be matured in a way arkd's CSV check honours,
        // so there's nothing to complete — surface that as Ignore rather than a
        // false failure. (Mirrors the guard in AwaitingCsvDelay_DoesNotAdvance…)
        var serverInfo = await setup.ClientTransport.GetServerInfoAsync(token);
        if (serverInfo.UnilateralExit.LockType != SequenceLockType.Height)
        {
            Assert.Ignore(
                $"Unilateral-exit delay is time-based (LockPeriod={serverInfo.UnilateralExit.LockPeriod}); " +
                "full-claim path requires a block-based delay. Expected block-based regtest config.");
        }

        // Phase 2: mature the CSV delay by mining, then keep progressing so the
        // claim tx is built + broadcast (Claimable → Claiming) and confirmed
        // (Claiming → Completed). Each iteration mines a block, which both
        // matures any remaining CSV and confirms the claim tx once broadcast.
        var csvBlocks = serverInfo.UnilateralExit.LockHeight + 2;
        TestContext.WriteLine($"[Exit] mining {csvBlocks} blocks to mature CSV delay");
        await DockerHelper.MineBlocks(csvBlocks, token);

        for (var step = 0; current!.State != ExitSessionState.Completed && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            TestContext.WriteLine(
                $"[Exit] claim step={step} state={current?.State} claimTxid={current?.ClaimTxid ?? "-"} " +
                $"fail={current?.FailReason ?? "-"}");
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Exit failed during claim: {current.FailReason}");
        }

        Assert.That(current!.State, Is.EqualTo(ExitSessionState.Completed),
            $"Exit should reach Completed; observed={current.State}, fail={current.FailReason ?? "-"}");
        Assert.That(current.ClaimTxid, Is.Not.Null.And.Not.Empty,
            "Completed session must record the on-chain claim txid");

        // The claimed funds must actually arrive at the claim address — the
        // whole point of a unilateral exit. Received == VTXO amount minus the
        // on-chain claim fee, so assert it's in a sane band rather than exact.
        var received = await DockerHelper.BitcoinGetReceivedByAddress(claimAddress.ToString(), minConf: 1, ct: token);
        TestContext.WriteLine($"[Exit] received at claim address: {received} (vtxo amount {vtxoAmount})");
        Assert.That(received, Is.GreaterThan(Money.Zero),
            "Claimed funds should be received at the on-chain claim address");
        Assert.That(received, Is.LessThanOrEqualTo(vtxoAmount),
            "Received amount cannot exceed the VTXO amount");
        Assert.That(received, Is.GreaterThan(vtxoAmount - Money.Satoshis(10_000)),
            $"Received {received} implausibly below VTXO amount {vtxoAmount} after fee");
    }

    /// <summary>
    /// Equivalent of go-sdk <c>TestUnilateralExit/preconfirmed vtxo</c>: exit a
    /// VTXO that was received off-chain (an Arkade tx, never batch-settled).
    /// Its ancestry chain is strictly deeper than a settled leaf's — it carries
    /// an extra off-chain <see cref="ChainedTxType.Ark"/> hop (and checkpoint)
    /// on top of the same on-chain commitment — so the broadcast path has more
    /// links to push. The exit must still reach <see cref="ExitSessionState.AwaitingCsvDelay"/>.
    /// </summary>
    [Test]
    [CancelAfter(420_000)]
    public async Task PreconfirmedVtxoExit_AdvancesToAwaitingCsvDelay(CancellationToken token)
    {
        await using var setup = await SettleAVtxoAsync(preconfirmSelfSend: true);
        Assert.That(setup.PreconfirmedVtxoOutpoint, Is.Not.Null,
            "Setup with preconfirmSelfSend:true must surface a preconfirmed VTXO outpoint");
        var outpoint = setup.PreconfirmedVtxoOutpoint!;
        var claimAddress = await GetFreshOnchainAddress();

        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId, [outpoint], claimAddress, token);
        Assert.That(sessions, Has.Count.EqualTo(1));
        Assert.That(sessions[0].State, Is.EqualTo(ExitSessionState.Broadcasting));

        // The branch must anchor on-chain (Commitment) AND include the off-chain
        // Arkade hop that makes this VTXO *preconfirmed* rather than settled —
        // that's exactly what distinguishes this case from the settled-leaf suites.
        var branch = await setup.VirtualTxStorage.GetBranchAsync(outpoint, token);
        TestContext.WriteLine(
            $"[Exit] preconfirmed chain depth={branch.Count}: " +
            string.Join(", ", branch.Select(t => t.Type)));
        Assert.That(branch.Any(tx => tx.Type == ChainedTxType.Commitment), Is.True,
            "Preconfirmed VTXO chain must still anchor at the on-chain commitment");
        Assert.That(branch.Any(tx => tx.Type == ChainedTxType.Ark), Is.True,
            "Preconfirmed VTXO chain must include the off-chain Arkade move that created it");

        // Drive the unroll like go-sdk's e2e loop (test/e2e/exit_test.go
        // "preconfirmed vtxo"): progress, mine a block, and pause briefly so the
        // Arkade server can detect the on-chain tree tx and broadcast the
        // checkpoint before we try to broadcast the leaf that spends it.
        ExitSession? current = null;
        for (var step = 0; !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(outpoint, token);
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Preconfirmed exit failed: {current.FailReason}");
            if (current?.State is ExitSessionState.AwaitingCsvDelay
                or ExitSessionState.Claimable or ExitSessionState.Claiming
                or ExitSessionState.Completed) break;
        }

        Assert.That(current!.State,
            Is.EqualTo(ExitSessionState.AwaitingCsvDelay)
                .Or.EqualTo(ExitSessionState.Claimable)
                .Or.EqualTo(ExitSessionState.Claiming)
                .Or.EqualTo(ExitSessionState.Completed),
            $"Preconfirmed exit should advance past Broadcasting; final={current.State}, " +
            $"fail={current.FailReason ?? "-"}");
    }

    /// <summary>
    /// Claim-after-expiry safety property: an exit whose branch is already
    /// broadcast + confirmed on-chain (AwaitingCsvDelay) must still complete
    /// even after the chain advances past the VTXO tree-expiry window
    /// (<c>ARKD_VTXO_TREE_EXPIRY=180</c> blocks in the regtest config). Once the
    /// user has spent their unilateral path out of the commitment, the operator's
    /// post-expiry sweep can no longer touch those outputs, so the funds remain
    /// claimable. This is only assertable now that expiry is block-based and can
    /// be matured by mining a bounded number of regtest blocks.
    /// </summary>
    [Test]
    [CancelAfter(300_000)]
    public async Task ExitConfirmedOnChain_StillCompletes_AfterVtxoTreeExpiry(CancellationToken token)
    {
        await using var setup = await SettleAVtxoAsync();
        var vtxo = (await setup.VtxoStorage.GetVtxos()).First(v => !v.IsSpent() && !v.Unrolled);
        var vtxoAmount = Money.Satoshis(vtxo.Amount);
        var claimAddress = await GetFreshOnchainAddress();

        await setup.ExitService.StartExitAsync(setup.WalletId, [vtxo.OutPoint], claimAddress, token);

        // Phase 1: broadcast the whole branch on-chain (Broadcasting → AwaitingCsvDelay).
        // After this, our path out of the commitment is spent and confirmed.
        ExitSession? current = null;
        for (var step = 0; !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Exit failed during broadcast: {current.FailReason}");
            if (current?.State is ExitSessionState.AwaitingCsvDelay
                or ExitSessionState.Claimable or ExitSessionState.Claiming
                or ExitSessionState.Completed) break;
        }
        Assert.That(current, Is.Not.Null);

        var serverInfo = await setup.ClientTransport.GetServerInfoAsync(token);
        if (serverInfo.UnilateralExit.LockType != SequenceLockType.Height)
            Assert.Ignore("Unilateral-exit delay is time-based; block-based regtest config expected.");

        // Phase 2: blow past the VTXO tree-expiry window. 200 > ARKD_VTXO_TREE_EXPIRY
        // (180) and also covers the 5-block CSV delay in one shot. If a post-expiry
        // operator sweep could strand an already-broadcast exit, the claim below
        // would fail — that's the regression this guards against.
        TestContext.WriteLine("[Exit] mining 200 blocks to pass VTXO tree-expiry (180) + CSV (5)");
        await DockerHelper.MineBlocks(200, token);

        for (var step = 0; current!.State != ExitSessionState.Completed && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            TestContext.WriteLine($"[Exit] post-expiry step={step} state={current?.State} fail={current?.FailReason ?? "-"}");
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Exit failed after tree expiry: {current.FailReason}");
        }

        Assert.That(current!.State, Is.EqualTo(ExitSessionState.Completed),
            $"An on-chain-confirmed exit must still complete after tree expiry; observed={current.State}");

        var received = await DockerHelper.BitcoinGetReceivedByAddress(claimAddress.ToString(), minConf: 1, ct: token);
        Assert.That(received, Is.GreaterThan(Money.Zero),
            "Funds must still reach the claim address after tree expiry");
        Assert.That(received, Is.LessThanOrEqualTo(vtxoAmount));
    }


    // [Test]
    // [CancelAfter(420_420)]
    // public async Task CanExitWithOperatorOffline(CancellationToken token)
    // {
    //     
    // }
    // ----- helpers --------------------------------------------------------

    /// <summary>
    /// Boards onchain funds into the wallet, runs intent generation + batch
    /// settlement, and wires up the unilateral-exit dependency graph against
    /// the same TestStorage so all subsequent service calls share state.
    /// Returns a disposable holding everything tests need to drive the exit.
    /// </summary>
    /// <param name="preconfirmSelfSend">
    /// When true, after the batch settle the wallet performs an off-chain Arkade
    /// self-send (checkpoint + Arkade tx, no batch), producing a fresh
    /// <b>preconfirmed</b> VTXO whose ancestry chain anchors at the settled
    /// VTXO's on-chain commitment through the checkpoint. Its outpoint is
    /// surfaced via <see cref="ExitTestSetup.PreconfirmedVtxoOutpoint"/>. This is
    /// the go-sdk <c>TestUnilateralExit/preconfirmed vtxo</c> scenario — the
    /// deeper-chain counterpart of the settled-leaf case.
    /// </param>
    private static async Task<ExitTestSetup> SettleAVtxoAsync(bool preconfirmSelfSend = false)
    {
        // ---- wallet + storage + transport ----
        // Build a ServiceCollection so the standard DI graph + the unilateral
        // exit storages share a single InMemory DbContextFactory.
        var safetyService = new AsyncSafetyService();
        var dbName = $"ExitTest_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<TestDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<ISafetyService>(safetyService);
        services.AddArkEfCoreStorage<TestDbContext>();
        services.AddSingleton<EfCoreVirtualTxStorage>();
        services.AddSingleton<IVirtualTxStorage>(sp => sp.GetRequiredService<EfCoreVirtualTxStorage>());
        services.AddSingleton<EfCoreExitSessionStorage>();
        services.AddSingleton<IExitSessionStorage>(sp => sp.GetRequiredService<EfCoreExitSessionStorage>());
        var sp = services.BuildServiceProvider();

        var vtxoStorage = sp.GetRequiredService<IVtxoStorage>();
        var contractStorage = sp.GetRequiredService<IContractStorage>();
        var intentStorage = sp.GetRequiredService<IIntentStorage>();
        var virtualTxStorage = sp.GetRequiredService<IVirtualTxStorage>();
        var exitSessionStorage = sp.GetRequiredService<IExitSessionStorage>();

        // Wait for the post-batch offchain VTXO to land in storage. The
        // boarding UTXO is Unrolled=true and gets consumed by the batch;
        // the new offchain output is Unrolled=false and must come through
        // VtxoSynchronizationService's subscription stream below.
        var settledVtxoTcs = new TaskCompletionSource();
        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && !vtxo.Unrolled) settledVtxoTcs.TrySetResult();
        };

        // Logger used across the settle + exit pipeline services so failures
        // surface in CI test output instead of being swallowed. Timestamps
        // make the output correlatable with arkd/bitcoind container logs.
        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss.fff ")
            .SetMinimumLevel(LogLevel.Debug));

        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();
        var contractService = new ContractService(walletProvider, contractStorage, clientTransport);

        // Stream VTXO updates from arkd into vtxoStorage so the post-batch
        // offchain VTXO becomes visible without manual indexer polling.
        var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage, clientTransport,
            [(IActiveScriptsProvider)vtxoStorage, (IActiveScriptsProvider)contractStorage]);
        await vtxoSync.StartAsync(CancellationToken.None);

        // ---- board: derive boarding contract, faucet, confirm, sync ----
        var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
            walletId, NextContractPurpose.Boarding, ContractActivityState.Active);
        var onchainAddress = boardingContract.GetOnchainAddress(info.Network).ToString();
        var fundingTxid = await DockerHelper.BitcoinSendToAddress(onchainAddress, Money.Satoshis(BoardingAmountSats));
        Assert.That(fundingTxid, Is.Not.Empty, "sendtoaddress should return a txid");

        await DockerHelper.MineBlocks(6);

        var utxoProvider = new EsploraBlockchain(SharedArkInfrastructure.ChopsticksEndpoint);
        var boardingSync = new BoardingUtxoSyncService(
            contractStorage, vtxoStorage, clientTransport, utxoProvider,
            loggerFactory.CreateLogger<BoardingUtxoSyncService>());

        // Poll until the boarding UTXO syncs as *confirmed* (ExpiresAt set), not
        // merely present. The Esplora backend (mempool API) can lag the 6 blocks
        // we just mined; BoardingUtxoSyncService stores a still-unconfirmed UTXO
        // with ExpiresAt=null, and SimpleIntentScheduler silently skips such
        // coins (arkd rejects unconfirmed inputs). Since the intent generation
        // cycle below runs only once per PollInterval, a row synced while the
        // funding tx looked unconfirmed would never settle within the test
        // budget. SyncAsync re-reads Esplora and upserts on every iteration, so
        // the row flips to confirmed as soon as the indexer catches up.
        ArkVtxo? syncedBoarding = null;
        for (var i = 0; i < 10 && syncedBoarding is null; i++)
        {
            await boardingSync.SyncAsync();
            syncedBoarding = (await vtxoStorage.GetVtxos())
                .FirstOrDefault(v => v.TransactionId == fundingTxid && v.ExpiresAt is not null);
            if (syncedBoarding is null) await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.That(syncedBoarding, Is.Not.Null,
            "Boarding UTXO should sync via Esplora as confirmed (ExpiresAt set)");

        // ---- settle: intent gen + submit + batch session ----
        var chainTimeProvider = new NBXplorerBlockchain(info.Network, SharedArkInfrastructure.NbxplorerEndpoint);
        var coinService = new CoinService(clientTransport, contractStorage,
        [
            new PaymentContractTransformer(walletProvider),
            new BoardingContractTransformer(walletProvider),
        ]);

        var newSuccessBatch = new TaskCompletionSource();
        intentStorage.IntentChanged += (_, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                newSuccessBatch.TrySetResult();
            // Surface terminal failures immediately. Waiting only for
            // BatchSucceeded turns any BatchFailed/Cancelled intent into a
            // silent 2-minute TimeoutException with no diagnostics; failing
            // fast with the recorded reason makes flakes attributable.
            else if (intent.State is ArkIntentState.BatchFailed or ArkIntentState.Cancelled)
                newSuccessBatch.TrySetException(new InvalidOperationException(
                    $"Intent {intent.IntentTxId} ended in {intent.State}: {intent.CancellationReason ?? "no reason recorded"}"));
        };

        var scheduler = new SimpleIntentScheduler(
            new DefaultFeeEstimator(clientTransport, chainTimeProvider),
            clientTransport,
            contractService,
            chainTimeProvider,
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions
            {
                Threshold = TimeSpan.FromHours(25),
                ThresholdHeight = 200,
            }),
            loggerFactory.CreateLogger<SimpleIntentScheduler>());

        var intentGeneration = new IntentGenerationService(
            clientTransport,
            new DefaultFeeEstimator(clientTransport, chainTimeProvider),
            coinService,
            walletProvider,
            intentStorage,
            safetyService,
            contractStorage,
            vtxoStorage,
            scheduler,
            new OptionsWrapper<IntentGenerationServiceOptions>(
                new IntentGenerationServiceOptions { PollInterval = TimeSpan.FromHours(5) }),
            loggerFactory.CreateLogger<IntentGenerationService>());
        await intentGeneration.StartAsync(CancellationToken.None);

        var intentSync = new IntentSynchronizationService(intentStorage, clientTransport, safetyService,
            loggerFactory.CreateLogger<IntentSynchronizationService>());
        await intentSync.StartAsync(CancellationToken.None);

        // SimpleIntentScheduler derives the SendToSelf output contract as
        // Inactive, so the SDK's stock post-batch polling (which filters
        // isActive=true) would skip it. Drive the polling ourselves
        // across *all* wallet contracts so the new !Unrolled VTXO lands
        // in storage and our settledVtxoTcs above can fire.
        var batchPolledTcs = new TaskCompletionSource();
        var postBatchHandler = new InlineEventHandler<PostBatchSessionEvent>(async (evt, ct) =>
        {
            if (evt.State != ActionState.Successful) return;
            var allContracts = await contractStorage.GetContracts(
                walletIds: [evt.Intent.WalletId], cancellationToken: ct);
            var allScripts = allContracts.Select(c => c.Script).ToHashSet();
            if (allScripts.Count == 0)
            {
                batchPolledTcs.TrySetResult();
                return;
            }
            // arkd commits the new VTXO to its indexer somewhere between
            // 0–10 seconds after BatchFinalized. Probe at a schedule that
            // covers that window without spinning hot. Bail early once the
            // !Unrolled VTXO has been observed (settledVtxoTcs) so we
            // don't keep polling after the test has moved on.
            foreach (var delay in new[] { 500, 1500, 3000, 5000, 8000 })
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                await vtxoSync.PollScriptsForVtxos(allScripts, after: null, ct);
                if (settledVtxoTcs.Task.IsCompleted) break;
            }
            batchPolledTcs.TrySetResult();
        });

        var batchManager = new BatchManagementService(
            intentStorage, clientTransport, vtxoStorage, contractStorage,
            walletProvider, coinService, safetyService,
            new IEventHandler<PostBatchSessionEvent>[] { postBatchHandler },
            loggerFactory.CreateLogger<BatchManagementService>());
        await batchManager.StartAsync(CancellationToken.None);

        // Auto-fetch the virtual-tx chain on every VTXO arrival. This is
        // opt-in in the SDK (AddVirtualTxAutoFetch) — the test exercises
        // it explicitly so StartExitAsync finds chain data already present.
        var virtualTxOptions = Options.Create(new VirtualTxOptions
        {
            DefaultMode = VirtualTxMode.Full,
            MinExitWorthAmount = 1000,
        });
        var chainProofProvider = new VtxoChainProofProvider(
            vtxoStorage, contractStorage, coinService, walletProvider, clientTransport,
            loggerFactory.CreateLogger<VtxoChainProofProvider>());
        var autoFetchService = new VtxoChainAutoFetchService(
            vtxoStorage,
            new VirtualTxService(clientTransport, virtualTxStorage, chainProofProvider,
                loggerFactory.CreateLogger<VirtualTxService>()),
            virtualTxOptions,
            loggerFactory.CreateLogger<VtxoChainAutoFetchService>());
        await autoFetchService.StartAsync(CancellationToken.None);

        await newSuccessBatch.Task.WaitAsync(TimeSpan.FromMinutes(2));
        // Wait until the post-batch poll completes AND the new !Unrolled
        // VTXO has been observed via VtxosChanged. The poll itself can
        // sometimes return empty if arkd's indexer hasn't committed yet —
        // give it a few extra seconds via VtxoSync's RoutinePoll.
        await batchPolledTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await settledVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // ---- optional: turn the settled VTXO into a *preconfirmed* one ----
        // An off-chain Arkade self-send spends the settled VTXO and lands a new
        // VTXO at a fresh Receive contract without going through a batch. That
        // new VTXO is "preconfirmed": its chain anchors at the same on-chain
        // commitment but through an extra checkpoint + Arkade tx, so exiting it
        // unilaterally exercises a strictly deeper broadcast path than the
        // settled-leaf case. (The autoFetch + vtxoSync services started above
        // observe the new VTXO and fetch its chain.)
        OutPoint? preconfirmedOutpoint = null;
        if (preconfirmSelfSend)
        {
            var receiveContract = await contractService.DeriveContract(
                walletId, NextContractPurpose.Receive);
            var receiveScript = receiveContract.GetArkAddress().ScriptPubKey.ToHex();

            var preconfirmedTcs = new TaskCompletionSource();
            void OnPreconfirmed(object? _, ArkVtxo v)
            {
                if (v.Script == receiveScript && !v.IsSpent() && !v.Unrolled)
                    preconfirmedTcs.TrySetResult();
            }
            vtxoStorage.VtxosChanged += OnPreconfirmed;

            var spendingService = new SpendingService(
                vtxoStorage, contractStorage, walletProvider, coinService,
                contractService, clientTransport, new DefaultCoinSelector(),
                safetyService, intentStorage);

            // Send well under the settled amount so input fees (amount * 0.01)
            // and change comfortably fit; MinExitWorthAmount (1000) keeps the
            // result worth exiting.
            await spendingService.Spend(walletId,
                [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(30_000), receiveContract.GetArkAddress())]);

            await preconfirmedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
            vtxoStorage.VtxosChanged -= OnPreconfirmed;

            var preconfirmedVtxo = (await vtxoStorage.GetVtxos(scripts: [receiveScript]))
                .First(v => !v.IsSpent() && !v.Unrolled);
            preconfirmedOutpoint = preconfirmedVtxo.OutPoint;
        }

        // ---- exit-side dependencies ----
        var explorerClient = new ExplorerClient(
            new NBXplorerNetworkProvider(info.Network.ChainName).GetBTC(),
            SharedArkInfrastructure.NbxplorerEndpoint);
        var broadcaster = new NBXplorerBlockchain(
            explorerClient, loggerFactory.CreateLogger<NBXplorerBlockchain>());
        var virtualTxService = new VirtualTxService(
            clientTransport, virtualTxStorage, chainProofProvider,
            loggerFactory.CreateLogger<VirtualTxService>());

        // Tree txs are v3 (TRUC). Bitcoin Core won't accept a v3 child of a
        // non-v3 parent (or any v3 tx with a 0-sat P2A anchor) on its own —
        // the broadcaster has to wrap each tree tx in a 1p1c CPFP package
        // via submitpackage. UnilateralExitService does that automatically
        // when given an IFeeWallet; without one it falls back to direct
        // sendrawtransaction and trips TRUC-violation. This test-side fee
        // wallet self-funds via bitcoin-cli sendtoaddress.
        var feeWallet = await TestFeeWallet.CreateFundedAsync();

        var exitService = new UnilateralExitService(
            clientTransport,
            virtualTxStorage,
            exitSessionStorage,
            vtxoStorage,
            contractStorage,
            broadcaster,
            walletProvider,
            virtualTxService,
            chainProofProvider,
            feeWallet: feeWallet,
            logger: loggerFactory.CreateLogger<UnilateralExitService>());

        return new ExitTestSetup(
            walletId,
            vtxoStorage,
            virtualTxStorage,
            exitSessionStorage,
            clientTransport,
            exitService,
            new IAsyncDisposable[]
            {
                intentGeneration, intentSync, batchManager, vtxoSync,
                new HostedServiceAdapter(autoFetchService),
            })
        {
            PreconfirmedVtxoOutpoint = preconfirmedOutpoint,
        };
    }

    private static async Task<BitcoinAddress> GetFreshOnchainAddress()
    {
        var addr = await DockerHelper.BitcoinCli(["getnewaddress"]);
        Assert.That(addr, Is.Not.Empty, "bitcoin-cli getnewaddress returned empty");
        return BitcoinAddress.Create(addr, Network.RegTest);
    }

    /// <summary>
    /// Minimal IEventHandler shim that delegates to a lambda. Lets a test
    /// inject post-batch behaviour without authoring a full handler class.
    /// </summary>
    private sealed class InlineEventHandler<T>(Func<T, CancellationToken, Task> handle) : IEventHandler<T> where T : class
    {
        public Task HandleAsync(T @event, CancellationToken cancellationToken = default)
            => handle(@event, cancellationToken);
    }

    /// <summary>Bridges an IHostedService into the IAsyncDisposable contract
    /// the test setup uses for cleanup.</summary>
    private sealed class HostedServiceAdapter(Microsoft.Extensions.Hosting.IHostedService inner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
            => await inner.StopAsync(CancellationToken.None);
    }

    private sealed class ExitTestSetup(
        string walletId,
        IVtxoStorage vtxoStorage,
        IVirtualTxStorage virtualTxStorage,
        IExitSessionStorage exitSessionStorage,
        NArk.Core.Transport.IClientTransport clientTransport,
        UnilateralExitService exitService,
        IReadOnlyCollection<IAsyncDisposable> disposables) : IAsyncDisposable
    {
        public string WalletId { get; } = walletId;
        public IVtxoStorage VtxoStorage { get; } = vtxoStorage;
        public IVirtualTxStorage VirtualTxStorage { get; } = virtualTxStorage;
        public IExitSessionStorage ExitSessionStorage { get; } = exitSessionStorage;
        public NArk.Core.Transport.IClientTransport ClientTransport { get; } = clientTransport;
        public UnilateralExitService ExitService { get; } = exitService;
        /// <summary>Address to which another wallet can send funds for this wallet to exit.</summary>
        public ArkAddress? ReceiveAddress { get; init; }

        /// <summary>
        /// Outpoint of the preconfirmed (off-chain, non-batched) VTXO produced
        /// when <see cref="SettleAVtxoAsync"/> is called with
        /// <c>preconfirmSelfSend: true</c>; otherwise <c>null</c>.
        /// </summary>
        public OutPoint? PreconfirmedVtxoOutpoint { get; init; }

        public async ValueTask DisposeAsync()
        {
            foreach (var d in disposables)
            {
                try { await d.DisposeAsync(); } catch { /* best effort */ }
            }
        }
    }

}

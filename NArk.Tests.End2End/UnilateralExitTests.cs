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
using NArk.Core.Exit;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.VirtualTxs;
using DefaultCoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Storage.EfCore.Storage;
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

        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, token);
        var sessionId = sessions[0].Id;

        // Drive the state machine. Each iteration: progress (broadcasts what
        // it can), mine 1 block to confirm what's in mempool, observe state.
        // Use GetByVtxoAsync (not GetActiveSessionsAsync) so a Failed
        // session is still surfaced — otherwise we'd silently lose it.
        ExitSession? current = null;
        for (var step = 0; step < 30 && !token.IsCancellationRequested; step++)
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

        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo.OutPoint], claimAddress, token);
        var sessionId = sessions[0].Id;

        // 1. Drive to AwaitingCsvDelay (broadcast + 1-block confirms).
        ExitSession? current = null;
        for (var step = 0; step < 30 && !token.IsCancellationRequested; step++)
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
    /// Full equivalent of go-sdk TestUnilateralExit/preconfirmed vtxo:
    /// a VTXO received via offchain Arkade payment (no local boarding or batch)
    /// is unrolled completely — Broadcasting → AwaitingCsvDelay → Completed —
    /// and the claim transaction is verified to have delivered BTC to the
    /// requested onchain address.
    /// </summary>
    /// <remarks>
    /// When the regtest UnilateralExit CSV is time-based the test skips the
    /// post-CSV phase (same caveat as
    /// <see cref="AwaitingCsvDelay_DoesNotAdvanceUntilDelayMatures"/>).
    /// </remarks>
    [Test]
    [Retry(3)]
    [CancelAfter(300_000)]
    public async Task ProgressExits_WorksForOffchainFundedVtxo(CancellationToken token)
    {
        await using var setup = await FundWalletOffchainForExitAsync();

        var vtxos = await setup.VtxoStorage.GetVtxos();
        var vtxo = vtxos.FirstOrDefault(v => !v.IsSpent() && !v.Unrolled);
        Assert.That(vtxo, Is.Not.Null,
            "Expected an unspent non-boarding VTXO after offchain fund; got: " +
            string.Join(", ", vtxos.Select(v =>
                $"{v.TransactionId[..8]}.. spent={v.IsSpent()} unrolled={v.Unrolled}")));

        var claimAddress = await GetFreshOnchainAddress();
        var sessions = await setup.ExitService.StartExitAsync(
            setup.WalletId, [vtxo!.OutPoint], claimAddress, token);

        Assert.That(sessions, Has.Count.EqualTo(1));
        Assert.That(sessions[0].State, Is.EqualTo(ExitSessionState.Broadcasting));

        var branch = await setup.VirtualTxStorage.GetBranchAsync(vtxo.OutPoint);
        Assert.That(branch, Has.Count.GreaterThan(0), "Virtual tx branch should be populated");

        // ── Phase 1: Broadcasting → AwaitingCsvDelay ─────────────────────
        ExitSession? current = null;
        for (var step = 0; step < 30 && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);

            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current is null) continue;

            TestContext.WriteLine(
                $"[Exit/offchain] step={step} state={current.State} " +
                $"nextTxIndex={current.NextTxIndex} fail={current.FailReason ?? "-"}");

            if (current.State is ExitSessionState.AwaitingCsvDelay
                or ExitSessionState.Claimable
                or ExitSessionState.Claiming
                or ExitSessionState.Completed)
                break;

            if (current.State == ExitSessionState.Failed)
                Assert.Fail($"Exit session failed during broadcast: {current.FailReason}");
        }

        Assert.That(current?.State,
            Is.EqualTo(ExitSessionState.AwaitingCsvDelay)
                .Or.EqualTo(ExitSessionState.Claimable)
                .Or.EqualTo(ExitSessionState.Claiming)
                .Or.EqualTo(ExitSessionState.Completed),
            $"Exit should advance past Broadcasting; state={current?.State}");

        // Already past AwaitingCsvDelay — CSV was very short, skip mining phase.
        if (current!.State is not ExitSessionState.AwaitingCsvDelay)
        {
            // Fall through to the Completed assertion below.
        }
        else
        {
            // ── Phase 2: mine CSV blocks ──────────────────────────────────
            var serverInfo = await setup.ClientTransport.GetServerInfoAsync(token);
            var unilateralExit = serverInfo.UnilateralExit;

            if (unilateralExit.LockType != SequenceLockType.Height)
            {
                TestContext.WriteLine(
                    $"[Exit/offchain] CSV delay is time-based " +
                    $"(LockPeriod={unilateralExit.LockPeriod}); " +
                    "skipping post-CSV claim assertion.");
                return;
            }

            var csvBlocks = unilateralExit.LockHeight + 2;
            TestContext.WriteLine($"[Exit/offchain] mining {csvBlocks} blocks to mature CSV");
            await DockerHelper.MineBlocks(csvBlocks, token);
        }

        // ── Phase 3: AwaitingCsvDelay/Claimable → Completed ──────────────
        for (var step = 0; step < 20 && !token.IsCancellationRequested; step++)
        {
            await setup.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);

            current = await setup.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);

            TestContext.WriteLine(
                $"[Exit/offchain claim] step={step} state={current?.State} " +
                $"fail={current?.FailReason ?? "-"}");

            if (current?.State is ExitSessionState.Completed) break;
            if (current?.State is ExitSessionState.Failed)
                Assert.Fail($"Exit session failed during claim: {current.FailReason}");

            await Task.Delay(500, token);
        }

        Assert.That(current?.State, Is.EqualTo(ExitSessionState.Completed),
            $"Exit should reach Completed; final state={current?.State}");

        // VTXO should be marked Unrolled in storage (mirrors go-sdk's
        // require.True(t, spent[0].Unrolled)).
        var finalVtxos = await setup.VtxoStorage.GetVtxos();
        var exitedVtxo = finalVtxos.FirstOrDefault(v => v.OutPoint == vtxo.OutPoint);
        Assert.That(exitedVtxo?.Unrolled, Is.True,
            "VTXO should be marked Unrolled after completed exit");

        // Claim address should have received BTC (mirrors go-sdk's carolUtxoEvent check).
        var receivedStr = await DockerHelper.Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "getreceivedbyaddress",
             claimAddress.ToString(), "0"]);
        Assert.That(decimal.Parse(receivedStr.Trim(),
                System.Globalization.CultureInfo.InvariantCulture),
            Is.GreaterThan(0m),
            $"Claim address {claimAddress} should have received BTC after exit completed");
    }

    // ----- helpers --------------------------------------------------------

    /// <summary>
    /// Equivalent of go-sdk TestUnilateralExit/preconfirmed vtxo:
    /// Wallet A sends to Wallet B offchain; Wallet B exits the VTXO before the
    /// batch commits it on-chain.
    /// </summary>
    /// <remarks>
    /// Currently blocked: arkd does not populate <c>PSBT_IN_TAP_KEY_SIG</c> in the
    /// tree-tx PSBTs for preconfirmed VTXOs because the MuSig2 co-signature is only
    /// produced during the batch round. <c>ParseVirtualTx</c> therefore throws
    /// "missing MuSig2 taproot key-path signature" and the exit fails.
    ///
    /// go-sdk handles this via an <c>ErrWaitingForConfirmation</c> retry loop that
    /// waits for the batch to fire before broadcasting the tree. NArk would need an
    /// equivalent: retry <c>ProgressExitsAsync</c> silently when signatures are
    /// absent instead of failing the session immediately.
    ///
    /// Re-enable once <c>ProgressExitsAsync</c> handles unsigned tree PSBTs by
    /// waiting for the next batch round rather than failing.
    /// </remarks>
    [Test]
    [Ignore("Blocked: preconfirmed VTXOs lack PSBT_IN_TAP_KEY_SIG until batch fires — " +
            "ProgressExitsAsync needs ErrWaitingForConfirmation-style retry")]
    [CancelAfter(300_000)]
    public async Task ProgressExits_WorksForPreconfirmedVtxo(CancellationToken token)
    {
        // ── Wallet A: funded, used only to send ──────────────────────
        var (safetyService, walletProvider, walletAId, vtxoStorage, contractService,
             contracts, clientTransport, _) = await FundedWalletHelper.GetFundedWallet();

        var intentStorage = TestStorage.CreateIntentStorage();
        var coinService = new CoinService(clientTransport, contracts,
            [new PaymentContractTransformer(walletProvider)]);
        var spendingService = new SpendingService(
            vtxoStorage, contracts, walletProvider, coinService, contractService,
            clientTransport, new DefaultCoinSelector(), safetyService, intentStorage);

        // ── Wallet B: fresh wallet + exit infrastructure ─────────────
        await using var walletB = await SetupReceiverForExitAsync();

        // Subscribe before the send so we catch the event synchronously.
        var vtxoReceivedTcs = new TaskCompletionSource<ArkVtxo>();
        walletB.VtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && !vtxo.Unrolled)
                vtxoReceivedTcs.TrySetResult(vtxo);
        };

        // ── Send A → B ───────────────────────────────────────────────
        await spendingService.Spend(walletAId,
            [new ArkTxOut(ArkTxOutType.Vtxo, 50_000, walletB.ReceiveAddress!)], token);

        var vtxo = await vtxoReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

        // ── Key assertion: VTXO must arrive before the batch commits ──
        if (!vtxo.Preconfirmed)
        {
            // Batch fired within the ARKD_ROUND_INTERVAL=10s window before we
            // could capture the preconfirmed state. This is an environmental
            // timing issue, not a code defect.
            Assert.Ignore(
                "VTXO was already settled when received " +
                "(batch committed before preconfirmed assertion; re-run or increase ARKD_ROUND_INTERVAL)");
            return;
        }
        Assert.That(vtxo.Preconfirmed, Is.True,
            "VTXO should be preconfirmed (batch not yet on-chain) at moment of receipt");

        // ── Wait for VtxoChainAutoFetchService to populate the branch ─
        // Failure here means arkd does not serve virtual-tx branches for
        // preconfirmed VTXOs — a real SDK gap, not a test bug.
        await TestWaiter.WaitFor(async () =>
        {
            var branch = await walletB.VirtualTxStorage.GetBranchAsync(vtxo.OutPoint);
            return branch.Count > 0;
        }, timeout: TimeSpan.FromSeconds(15), ct: token);

        // ── Full exit: Broadcasting → CSV → Completed ────────────────
        var claimAddress = await GetFreshOnchainAddress();
        var sessions = await walletB.ExitService.StartExitAsync(
            walletB.WalletId, [vtxo.OutPoint], claimAddress, token);

        Assert.That(sessions, Has.Count.EqualTo(1));
        Assert.That(sessions[0].State, Is.EqualTo(ExitSessionState.Broadcasting));

        ExitSession? current = null;
        for (var step = 0; step < 30 && !token.IsCancellationRequested; step++)
        {
            await walletB.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await walletB.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current is null) continue;
            TestContext.WriteLine(
                $"[Exit/preconfirmed] step={step} state={current.State} " +
                $"fail={current.FailReason ?? "-"}");
            if (current.State is ExitSessionState.AwaitingCsvDelay or ExitSessionState.Claimable
                or ExitSessionState.Claiming or ExitSessionState.Completed) break;
            if (current.State == ExitSessionState.Failed)
                Assert.Fail($"Exit failed during broadcast: {current.FailReason}");
        }

        if (current?.State == ExitSessionState.AwaitingCsvDelay)
        {
            var serverInfo = await walletB.ClientTransport.GetServerInfoAsync(token);
            var unilateralExit = serverInfo.UnilateralExit;
            if (unilateralExit.LockType != SequenceLockType.Height)
            {
                TestContext.WriteLine("[Exit/preconfirmed] Time-based CSV; skipping post-CSV assertion");
                return;
            }
            await DockerHelper.MineBlocks(unilateralExit.LockHeight + 2, token);
        }

        for (var step = 0; step < 20 && !token.IsCancellationRequested; step++)
        {
            await walletB.ExitService.ProgressExitsAsync(token);
            await DockerHelper.MineBlocks(1, token);
            current = await walletB.ExitSessionStorage.GetByVtxoAsync(vtxo.OutPoint, token);
            if (current?.State == ExitSessionState.Completed) break;
            if (current?.State == ExitSessionState.Failed)
                Assert.Fail($"Exit failed during claim: {current.FailReason}");
            await Task.Delay(500, token);
        }

        Assert.That(current?.State, Is.EqualTo(ExitSessionState.Completed),
            $"Exit should reach Completed; state={current?.State}");

        var finalVtxos = await walletB.VtxoStorage.GetVtxos();
        Assert.That(finalVtxos.FirstOrDefault(v => v.OutPoint == vtxo.OutPoint)?.Unrolled, Is.True,
            "VTXO should be marked Unrolled after exit completes");

        var received = await DockerHelper.Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "getreceivedbyaddress", claimAddress.ToString(), "0"]);
        Assert.That(
            decimal.Parse(received.Trim(), System.Globalization.CultureInfo.InvariantCulture),
            Is.GreaterThan(0m),
            $"Claim address should have received BTC after exit completed");
    }

    /// <summary>
    /// Sets up a wallet with VtxoSync + exit infrastructure but no initial funding.
    /// The returned <see cref="ExitTestSetup.ReceiveAddress"/> can be used by another
    /// wallet to send a preconfirmed VTXO to this wallet.
    /// </summary>
    private static async Task<ExitTestSetup> SetupReceiverForExitAsync()
    {
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var vtxoStorage = storage.VtxoStorage;
        var contractStorage = storage.ContractStorage;
        var virtualTxStorage = new InMemoryVirtualTxStorage();
        var exitSessionStorage = new InMemoryExitSessionStorage();

        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();
        var contractService = new ContractService(walletProvider, contractStorage, clientTransport);

        var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage, clientTransport,
            [(IActiveScriptsProvider)vtxoStorage, (IActiveScriptsProvider)contractStorage]);
        await vtxoSync.StartAsync(CancellationToken.None);

        var receiveContract = await contractService.DeriveContract(
            walletId, NextContractPurpose.Receive, ContractActivityState.Active);
        var receiveAddress = receiveContract.GetArkAddress();

        var virtualTxService = new VirtualTxService(
            clientTransport, virtualTxStorage,
            loggerFactory.CreateLogger<VirtualTxService>());

        var autoFetchService = new VtxoChainAutoFetchService(
            vtxoStorage,
            virtualTxService,
            Options.Create(new VirtualTxOptions
            {
                DefaultMode = VirtualTxMode.Full,
                MinExitWorthAmount = 1000,
            }),
            loggerFactory.CreateLogger<VtxoChainAutoFetchService>());
        await autoFetchService.StartAsync(CancellationToken.None);

        var explorerClient = new ExplorerClient(
            new NBXplorerNetworkProvider(info.Network.ChainName).GetBTC(),
            SharedArkInfrastructure.NbxplorerEndpoint);
        var broadcaster = new NBXplorerBlockchain(
            explorerClient, loggerFactory.CreateLogger<NBXplorerBlockchain>());
        var feeWallet = await TestFeeWallet.CreateFundedAsync();
        var exitService = new UnilateralExitService(
            clientTransport, virtualTxStorage, exitSessionStorage,
            vtxoStorage, contractStorage, broadcaster, walletProvider,
            virtualTxService, feeWallet: feeWallet,
            logger: loggerFactory.CreateLogger<UnilateralExitService>());

        return new ExitTestSetup(
            walletId, vtxoStorage, virtualTxStorage, exitSessionStorage,
            clientTransport, exitService,
            [vtxoSync, new HostedServiceAdapter(autoFetchService)])
        {
            ReceiveAddress = receiveAddress
        };
    }

    /// <summary>
    /// Funds a wallet via Fulmine offchain send and wires the exit infrastructure
    /// around it. Unlike <see cref="SettleAVtxoAsync"/>, no local boarding or
    /// batch is required — the VTXO arrives directly from Fulmine's settled batch.
    /// </summary>
    private static async Task<ExitTestSetup> FundWalletOffchainForExitAsync()
    {
        var safetyService = new AsyncSafetyService();
        var storage = new TestStorage(safetyService);
        var vtxoStorage = storage.VtxoStorage;
        var contractStorage = storage.ContractStorage;
        var virtualTxStorage = new InMemoryVirtualTxStorage();
        var exitSessionStorage = new InMemoryExitSessionStorage();

        var receivedVtxoTcs = new TaskCompletionSource();
        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && !vtxo.Unrolled) receivedVtxoTcs.TrySetResult();
        };

        var clientTransport = new GrpcClientTransport(SharedArkInfrastructure.ArkdEndpoint.ToString());
        var info = await clientTransport.GetServerInfoAsync();
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        var walletProvider = new InMemoryWalletProvider(clientTransport);
        var walletId = await walletProvider.CreateTestWallet();
        var contractService = new ContractService(walletProvider, contractStorage, clientTransport);

        var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage, clientTransport,
            [(IActiveScriptsProvider)vtxoStorage, (IActiveScriptsProvider)contractStorage]);
        await vtxoSync.StartAsync(CancellationToken.None);

        var receiveContract = await contractService.DeriveContract(
            walletId, NextContractPurpose.Receive, ContractActivityState.Active);
        var receiveAddress = receiveContract.GetArkAddress().ToString(isMainnet: false);

        var virtualTxService = new VirtualTxService(
            clientTransport, virtualTxStorage,
            loggerFactory.CreateLogger<VirtualTxService>());

        var autoFetchService = new VtxoChainAutoFetchService(
            vtxoStorage,
            virtualTxService,
            Options.Create(new VirtualTxOptions
            {
                DefaultMode = VirtualTxMode.Full,
                MinExitWorthAmount = 1000,
            }),
            loggerFactory.CreateLogger<VtxoChainAutoFetchService>());
        await autoFetchService.StartAsync(CancellationToken.None);

        await DockerHelper.SendArkdNoteTo(receiveAddress, BoardingAmountSats);
        await receivedVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Wait until VtxoChainAutoFetchService has populated the virtual tx branch.
        await TestWaiter.WaitFor(async () =>
        {
            var vtxos = await vtxoStorage.GetVtxos();
            var vtxo = vtxos.FirstOrDefault(v => !v.IsSpent() && !v.Unrolled);
            if (vtxo is null) return false;
            var branch = await virtualTxStorage.GetBranchAsync(vtxo.OutPoint);
            return branch.Count > 0;
        }, timeout: TimeSpan.FromSeconds(20));

        var explorerClient = new ExplorerClient(
            new NBXplorerNetworkProvider(info.Network.ChainName).GetBTC(),
            SharedArkInfrastructure.NbxplorerEndpoint);
        var broadcaster = new NBXplorerBlockchain(
            explorerClient, loggerFactory.CreateLogger<NBXplorerBlockchain>());

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
            feeWallet: feeWallet,
            logger: loggerFactory.CreateLogger<UnilateralExitService>());

        return new ExitTestSetup(
            walletId, vtxoStorage, virtualTxStorage, exitSessionStorage, clientTransport,
            exitService,
            [vtxoSync, new HostedServiceAdapter(autoFetchService)]);
    }

    /// <summary>
    /// Boards onchain funds into the wallet, runs intent generation + batch
    /// settlement, and wires up the unilateral-exit dependency graph against
    /// the same TestStorage so all subsequent service calls share state.
    /// Returns a disposable holding everything tests need to drive the exit.
    /// </summary>
    private static async Task<ExitTestSetup> SettleAVtxoAsync()
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
        var autoFetchService = new VtxoChainAutoFetchService(
            vtxoStorage,
            new VirtualTxService(clientTransport, virtualTxStorage,
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

        // ---- exit-side dependencies ----
        var explorerClient = new ExplorerClient(
            new NBXplorerNetworkProvider(info.Network.ChainName).GetBTC(),
            SharedArkInfrastructure.NbxplorerEndpoint);
        var broadcaster = new NBXplorerBlockchain(
            explorerClient, loggerFactory.CreateLogger<NBXplorerBlockchain>());
        var virtualTxService = new VirtualTxService(
            clientTransport, virtualTxStorage,
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
            });
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

        public async ValueTask DisposeAsync()
        {
            foreach (var d in disposables)
            {
                try { await d.DisposeAsync(); } catch { /* best effort */ }
            }
        }
    }

}

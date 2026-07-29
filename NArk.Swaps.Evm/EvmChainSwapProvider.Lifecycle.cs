using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Evm.Dex;
using NArk.Swaps.Evm.Models;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;

namespace NArk.Swaps.Evm;

/// <summary>
/// Lifecycle: start/stop, the REST poll loop (safety net), the persistent Boltz websocket
/// (primary status push), and the storage/VTXO change notifications. Mirrors
/// <c>BoltzSwapProvider.Lifecycle.cs</c>&apos;s split.
/// </summary>
public partial class EvmChainSwapProvider
{
    // ─── Lifecycle: websocket push (primary) + REST poll loop (safety net) ─────────

    public async Task StartAsync(CancellationToken ct)
    {
        // Seed the script→swap map from storage so a VTXO arriving before the first routine
        // poll (e.g. right after a restart, for a swap that was already active) still dispatches
        // correctly — mirrors BoltzSwapProvider.Lifecycle.cs's StartAsync exactly.
        try
        {
            var existingActiveSwaps = await _swapStorage.GetSwaps(
                swapTypes: [ArkSwapType.ChainArkToEvm, ArkSwapType.ChainEvmToArk], active: true, cancellationToken: ct);
            foreach (var swap in existingActiveSwaps.Where(s => s.ProviderId == Id && !string.IsNullOrEmpty(s.ContractScript)))
                _scriptToSwapId[swap.ContractScript] = swap.SwapId;
            _logger?.LogInformation("Seeded script→swap map with {Count} active swap(s)", _scriptToSwapId.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to seed script→swap map from storage; RunPollLoopAsync will pick up on next tick");
        }

        _pollingTask = RunPollLoopAsync(_shutdownCts.Token);
        _websocketTask = RunWebsocketLoop(_shutdownCts.Token);
        _wsTriggerReaderTask = RunWsTriggerReaderAsync(_shutdownCts.Token);
        _intentStorage.IntentChanged += OnRefundIntentChanged;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _intentStorage.IntentChanged -= OnRefundIntentChanged;
        _shutdownCts.Cancel();
        _wsTriggerChannel.Writer.TryComplete();
        await Drain(_pollingTask);
        await Drain(_websocketTask);
        await Drain(_wsTriggerReaderTask);
    }

    /// <summary>
    /// Awaits a background task, swallowing any exception. Once shutdown has been requested,
    /// a fault from work that was interrupted mid-flight (e.g. a refund's nested await noticing
    /// the now-cancelled <see cref="_shutdownCts"/> token deeper in its call chain than the
    /// per-tick try/catch in <see cref="RunPollLoopAsync"/> covers) is an artifact of the
    /// cancellation itself, not a real symptom — mirrors <c>BoltzSwapProvider.Lifecycle.cs</c>'s
    /// own <c>Drain</c> helper.
    /// </summary>
    private static async Task Drain(Task? task)
    {
        if (task is null) return;
        try { await task; }
        catch { /* expected on cancel */ }
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var swaps = await _swapStorage.GetSwaps(
                    swapTypes: [ArkSwapType.ChainArkToEvm, ArkSwapType.ChainEvmToArk],
                    active: true,
                    cancellationToken: ct);
                var ourSwaps = swaps.Where(s => s.ProviderId == Id).ToList();

                // Keep the persistent websocket's subscriptions in sync with the active set.
                // Covers both "new swap since the websocket last (re)connected" and the initial
                // race against RunWebsocketLoop's own startup snapshot — self-heals within one
                // tick either way, no separate seeding needed.
                var newlyActive = ourSwaps
                    .Select(s => s.SwapId)
                    .Where(id => _swapsIdToWatch.TryAdd(id, 0))
                    .ToArray();
                if (newlyActive.Length > 0)
                    await SubscribeOnWebsocketAsync(newlyActive, ct);

                foreach (var swap in ourSwaps)
                {
                    await PollSwapAsync(swap, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger?.LogError(ex, "EVM swap poll loop iteration failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }

    private async Task PollSwapAsync(ArkSwap swap, CancellationToken ct)
    {
        var status = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
        if (status == null)
            return;

        var action = EvmChainOperationClassifier.Classify(swap, status.Status);
        switch (action)
        {
            case EvmSwapAction.CanRenegotiateChain:
            {
                if (await TryRenegotiateChainSwap(swap, ct))
                {
                    // Renegotiation accepted — re-poll immediately (against the freshly
                    // persisted ExpectedAmount) so the claim fires this cycle rather than
                    // waiting for the next tick, mirroring BoltzSwapProvider's equivalent path.
                    var refreshed = (await _swapStorage.GetSwaps(swapIds: [swap.SwapId], cancellationToken: ct))
                        .FirstOrDefault() ?? swap;
                    await PollSwapAsync(refreshed, ct);
                    return;
                }

                // Boltz refused the quote (funded amount outside its limits) — fall back to
                // refunding whichever side we locked, mirroring BoltzSwapProvider's fallback.
                if (swap.SwapType == ArkSwapType.ChainArkToEvm)
                    await TryCoopRefundArkToEvm(swap, ct);
                else
                    await TryRefundEvmLockupAsync(swap, ct);
                return;
            }
            case EvmSwapAction.CanClaimEvmLockup:
                await TryClaimEvmLockupAsync(swap, ct);
                return;
            case EvmSwapAction.CanRefundEvmLockup:
                await TryRefundEvmLockupAsync(swap, ct);
                return;
            case EvmSwapAction.CanClaimArkLockup:
                // No explicit action: PersistSwapAsync already imported this VHTLC via
                // IContractService.ImportContract(AwaitingFundsBeforeDeactivate), which puts its
                // script in VtxoSynchronizationService's watched set. Once the VTXO lands, the
                // wallet-wide SweeperService (always running — registered unconditionally by
                // AddArkCoreServices) claims it via SwapSweepPolicy/VHTLCContractTransformer,
                // exactly like BoltzSwapProvider's ChainBtcToArk direction already does — see
                // InitiateBtcToArkChainSwap's "Import VHTLC contract for sweeper to claim" comment.
                break;
            case EvmSwapAction.CanRefundArkLockup:
                await TryCoopRefundArkToEvm(swap, ct);
                return;
        }

        var terminal = BoltzSwapStatus.ToArkSwapStatus(status.Status);
        if (terminal != null && terminal != swap.Status)
        {
            await _swapStorage.UpdateSwapStatus(swap.WalletId, swap.SwapId, terminal.Value, status.FailureReason, ct);
            SwapStatusChanged?.Invoke(this,
                new SwapStatusChangedEvent(swap.SwapId, swap.WalletId, Id, terminal.Value, status.FailureReason));

            if (terminal.Value.IsTerminalState())
            {
                _swapsIdToWatch.TryRemove(swap.SwapId, out _);
                await UnsubscribeOnWebsocketAsync([swap.SwapId], ct);
            }
        }
    }

    /// <summary>Persists a terminal status transition and unsubscribes the swap from the
    /// persistent websocket — the shared cleanup both the cooperative and batch-intent refund
    /// paths need once a swap reaches <see cref="ArkSwapStatus.Refunded"/>/<see cref="ArkSwapStatus.Failed"/>.</summary>
    private async Task MarkSwapTerminalAsync(ArkSwap swap, ArkSwapStatus status, string? failReason, CancellationToken ct)
    {
        var updated = swap with { Status = status, FailReason = failReason, UpdatedAt = DateTimeOffset.UtcNow };
        await _swapStorage.SaveSwap(swap.WalletId, updated, ct);
        SwapStatusChanged?.Invoke(this, new SwapStatusChangedEvent(swap.SwapId, swap.WalletId, Id, status, failReason));
        _swapsIdToWatch.TryRemove(swap.SwapId, out _);
        await UnsubscribeOnWebsocketAsync([swap.SwapId], ct);
    }
    // ─── WebSocket ─────────────────────────────────────────────────

    /// <summary>
    /// Single long-lived task owning the persistent Boltz websocket connection for the EVM
    /// swap leg. Mirrors <c>BoltzSwapProvider.RunWebsocketLoop</c> — one connection, repeated
    /// subscribe/unsubscribe ops keyed by swap id (per
    /// https://api.docs.boltz.exchange/api-v2.html#websocket) — reconnects with a 5s backoff
    /// and re-subscribes to the then-current <see cref="_swapsIdToWatch"/> snapshot.
    /// </summary>
    private async Task RunWebsocketLoop(CancellationToken ct)
    {
        var wsUri = _boltzClient.DeriveWebSocketUri();
        while (!ct.IsCancellationRequested)
        {
            BoltzWebsocketClient? client = null;
            try
            {
                _logger?.LogInformation("Connecting to Boltz websocket at {Uri} for EVM chain swaps", wsUri);
                client = new BoltzWebsocketClient(wsUri);
                client.OnAnyEventReceived += OnSwapEventReceived;
                await client.ConnectAsync(ct);

                string[] initialSubs;
                await _websocketLock.WaitAsync(ct);
                try
                {
                    _websocket = client;
                    initialSubs = _swapsIdToWatch.Keys.ToArray();
                }
                finally
                {
                    _websocketLock.Release();
                }

                if (initialSubs.Length > 0)
                {
                    await client.SubscribeAsync(initialSubs, ct);
                    _logger?.LogInformation(
                        "EVM swap websocket connected, subscribed to {Count} swap(s): [{SwapIds}]",
                        initialSubs.Length, string.Join(", ", initialSubs));
                }
                else
                {
                    _logger?.LogInformation("EVM swap websocket connected, no active swaps to subscribe yet");
                }

                await client.WaitUntilDisconnected(ct);
                _logger?.LogWarning("EVM swap websocket disconnected");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "EVM swap websocket error, reconnecting in 5s");
            }
            finally
            {
                await _websocketLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (client is not null) client.OnAnyEventReceived -= OnSwapEventReceived;
                    if (ReferenceEquals(_websocket, client)) _websocket = null;
                }
                finally
                {
                    _websocketLock.Release();
                }
                if (client is not null) await client.DisposeAsync();
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(5000, ct);
        }
    }

    /// <summary>Subscribes additional swap ids on the current persistent websocket. No-ops when
    /// disconnected — the reconnect loop picks the ids up from <see cref="_swapsIdToWatch"/>.</summary>
    private async Task SubscribeOnWebsocketAsync(IReadOnlyList<string> swapIds, CancellationToken ct)
    {
        if (swapIds.Count == 0) return;
        await _websocketLock.WaitAsync(ct);
        try
        {
            if (_websocket is null)
            {
                _logger?.LogDebug(
                    "Skipping EVM websocket Subscribe: connection not yet up; reconnect loop will pick up [{SwapIds}]",
                    string.Join(", ", swapIds));
                return;
            }
            await _websocket.SubscribeAsync(swapIds.ToArray(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "EVM websocket Subscribe failed for [{SwapIds}]; reconnect loop will retry",
                string.Join(", ", swapIds));
        }
        finally
        {
            _websocketLock.Release();
        }
    }

    /// <summary>Unsubscribes swap ids from the current persistent websocket. Best-effort —
    /// leaving a terminal swap subscribed only costs a stray no-op push.</summary>
    private async Task UnsubscribeOnWebsocketAsync(IReadOnlyList<string> swapIds, CancellationToken ct)
    {
        if (swapIds.Count == 0) return;
        await _websocketLock.WaitAsync(ct);
        try
        {
            if (_websocket is null) return;
            await _websocket.UnsubscribeAsync(swapIds.ToArray(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "EVM websocket Unsubscribe failed for [{SwapIds}]; non-fatal",
                string.Join(", ", swapIds));
        }
        finally
        {
            _websocketLock.Release();
        }
    }

    private Task OnSwapEventReceived(WebSocketResponse? response)
    {
        try
        {
            if (response is { Event: "update", Channel: "swap.update", Args.Count: > 0 })
            {
                var swapUpdate = response.Args[0];
                var id = swapUpdate?["id"]?.GetValue<string>();
                if (id is not null)
                {
                    _logger?.LogDebug("EVM websocket event: swap {SwapId} status update", id);
                    _wsTriggerChannel.Writer.TryWrite(id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing EVM websocket event");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Decouples websocket event receipt from swap processing: <see cref="OnSwapEventReceived"/>
    /// only enqueues the swap id, this loop does the actual (potentially slow — REST call to
    /// Boltz, on-chain claim/refund) work, so a slow poll never delays draining the next
    /// websocket message.
    /// </summary>
    private async Task RunWsTriggerReaderAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var swapId in _wsTriggerChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var swaps = await _swapStorage.GetSwaps(swapIds: [swapId], cancellationToken: ct);
                    var swap = swaps.FirstOrDefault(s => s.ProviderId == Id);
                    if (swap is null) continue;
                    await PollSwapAsync(swap, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogError(ex, "Websocket-triggered poll failed for swap {SwapId}", swapId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Called by <c>SwapsManagementService</c> when a VTXO changes on ANY tracked script across
    /// ALL registered providers, not just ours — mirrors <c>BoltzSwapProvider.NotifyVtxoChanged</c>.
    /// Scripts belonging to other providers simply won't be in <see cref="_scriptToSwapId"/>, so
    /// this naturally no-ops for them.
    /// </summary>
    public void NotifyVtxoChanged(ArkVtxo vtxo)
    {
        try
        {
            if (_scriptToSwapId.TryGetValue(vtxo.Script, out var id))
            {
                _logger?.LogInformation(
                    "NotifyVtxoChanged: VTXO {Outpoint} on swap {SwapId}'s contract script (amount={Amount}, spent={Spent}) — triggering status poll",
                    vtxo.OutPoint, id, vtxo.Amount, vtxo.SpentByTransactionId is not null);
                _wsTriggerChannel.Writer.TryWrite(id);
            }
            else
            {
                _logger?.LogDebug(
                    "NotifyVtxoChanged: VTXO {Outpoint} on script {Script} — no swap mapping, ignoring",
                    vtxo.OutPoint, vtxo.Script);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NotifyVtxoChanged: error dispatching VTXO {Outpoint}", vtxo.OutPoint);
        }
    }

    /// <summary>
    /// Called by <c>SwapsManagementService</c> when ANY swap record changes in storage, not just
    /// ours — mirrors <c>BoltzSwapProvider.NotifySwapChanged</c>. The unconditional trigger write
    /// at the end for a foreign swap id is harmless: <see cref="RunWsTriggerReaderAsync"/> already
    /// filters by <c>s.ProviderId == Id</c>.
    /// </summary>
    public void NotifySwapChanged(ArkSwap swap)
    {
        if (!string.IsNullOrEmpty(swap.ContractScript))
        {
            if (swap.Status.IsTerminalState())
            {
                if (_scriptToSwapId.TryRemove(swap.ContractScript, out _))
                    _logger?.LogInformation(
                        "NotifySwapChanged: swap {SwapId} reached terminal {Status} — removed contract-script mapping",
                        swap.SwapId, swap.Status);
            }
            else
            {
                _scriptToSwapId[swap.ContractScript] = swap.SwapId;
                _logger?.LogDebug(
                    "NotifySwapChanged: swap {SwapId} storage event (type={Type}, status={Status}) — map now has {Count} entries",
                    swap.SwapId, swap.SwapType, swap.Status, _scriptToSwapId.Count);
            }
        }

        _wsTriggerChannel.Writer.TryWrite(swap.SwapId);
    }

    public async ValueTask DisposeAsync()
    {
        _intentStorage.IntentChanged -= OnRefundIntentChanged;
        _shutdownCts.Cancel();
        _wsTriggerChannel.Writer.TryComplete();
        await Drain(_pollingTask);
        await Drain(_websocketTask);
        await Drain(_wsTriggerReaderTask);
        _shutdownCts.Dispose();
        _nonceGuard.Dispose();
        _evmClientInitLock.Dispose();
        _websocketLock.Dispose();
    }
}

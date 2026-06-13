using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Recovery;
using NArk.Core.Transport;

namespace NArk.Core.Services;

/// <summary>
/// Keeps every SingleKey wallet's advertised "Default" contract aligned with the
/// CURRENT arkd signer.
/// <para>
/// A SingleKey wallet's Default contract is derived from <c>ArkServerInfo.SignerKey</c>.
/// When arkd rotates its signer, the old-signer Default becomes stale: the new-signer
/// Default must be derived (and advertised), and the stale one superseded so only one
/// row is the advertised default.
/// </para>
/// <para>
/// Triggers:
/// <list type="bullet">
/// <item><see cref="IWalletStorage.WalletSaved"/> (wallet created/updated) → reconcile that one wallet.</item>
/// <item><see cref="IServerInfoCacheInvalidation.ServerInfoChanged"/> (signer rotated) → reconcile ALL SingleKey wallets.</item>
/// <item>Startup pass on <see cref="StartAsync"/> → reconcile ALL SingleKey wallets (covers wallets rotated while offline).</item>
/// </list>
/// </para>
/// <para>
/// <b>Supersede semantics:</b> funds safety does NOT depend on the deactivation — the sweeper
/// gathers coins by VTXO script regardless of Active state — so deactivating stale
/// <c>Source="Default"</c> rows is purely about which row is the advertised default.
/// </para>
/// <para>
/// Lifecycle mirrors <see cref="SweeperService"/>: event handlers only enqueue (non-blocking),
/// a background worker drains the channel, subscribe in <see cref="StartAsync"/>, unsubscribe +
/// cancel in <see cref="DisposeAsync"/>.
/// </para>
/// </summary>
public class ContractReconciliationService(
    IWalletStorage walletStorage,
    IContractStorage contractStorage,
    ISingleKeyDefaultEnsurer defaultEnsurer,
    IServerInfoCacheInvalidation serverInfoCacheInvalidation,
    IClientTransport clientTransport,
    ILogger<ContractReconciliationService>? logger = null,
    TimeSpan? reconcileAllRetryDelay = null) : IAsyncDisposable, IDestinationSafetyNotifier
{
    /// <inheritdoc/>
    public event EventHandler<DestinationDisabledEventArgs>? DestinationDisabled;

    private const string SourceMetadataKey = "Source";
    private const string DefaultSourceValue = "Default";

    /// <summary>Max times a wholesale ReconcileAll pass (e.g. startup with arkd unreachable) is retried.</summary>
    private const int MaxReconcileAllRetries = 3;
    private static readonly TimeSpan DefaultReconcileAllRetryDelay = TimeSpan.FromSeconds(30);

    // Delay between wholesale-ReconcileAll retries. Overridable (tests use a short delay); defaults to 30s.
    private readonly TimeSpan _reconcileAllRetryDelay = reconcileAllRetryDelay ?? DefaultReconcileAllRetryDelay;

    private abstract record ReconcileJob;
    private sealed record ReconcileWalletJob(string WalletId) : ReconcileJob;
    private sealed record ReconcileAllJob(int Attempt = 0) : ReconcileJob;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<ReconcileJob> _jobs = Channel.CreateUnbounded<ReconcileJob>();

    private Task? _workerTask;
    private CancellationTokenSource? _multiToken;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogInformation("Starting contract reconciliation service");
        _multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _workerTask = DoReconciliationLoop(_multiToken.Token);

        walletStorage.WalletSaved += OnWalletSaved;
        serverInfoCacheInvalidation.ServerInfoChanged += OnServerInfoChanged;

        // Startup pass: reconcile all SingleKey wallets (covers wallets rotated while offline).
        _jobs.Writer.TryWrite(new ReconcileAllJob());

        logger?.LogDebug("Contract reconciliation service started");
        return Task.CompletedTask;
    }

    private async Task DoReconciliationLoop(CancellationToken loopShutdownToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(loopShutdownToken))
        {
            try
            {
                await (job switch
                {
                    ReconcileWalletJob walletJob => ReconcileWalletAsync(walletJob.WalletId, loopShutdownToken),
                    ReconcileAllJob => ReconcileAllAsync(loopShutdownToken),
                    _ => Task.CompletedTask,
                });
            }
            catch (OperationCanceledException) when (loopShutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                logger?.LogWarning(0, e, "Error during reconciliation loop execution for job {JobType}", job.GetType().Name);

                // A wholesale ReconcileAll failure means we may have missed rotations that happened
                // while offline. The common cause is arkd being unreachable at boot: that surfaces
                // as the up-front GetServerInfoAsync availability probe in ReconcileAllAsync throwing
                // (NOT LoadAllWallets, which is pure DB and doesn't depend on arkd). Requeue after a
                // short delay, bounded, so a transient outage self-heals without a tight retry loop.
                // Per-wallet failures during a reachable-backend pass don't reach here (ReconcileAllAsync
                // absorbs them), so this only fires on a true whole-pass failure.
                if (job is ReconcileAllJob allJob && allJob.Attempt < MaxReconcileAllRetries)
                {
                    ScheduleReconcileAllRetry(allJob.Attempt + 1, loopShutdownToken);
                }
            }
        }
    }

    /// <summary>
    /// Requeues a <see cref="ReconcileAllJob"/> after <see cref="ReconcileAllRetryDelay"/>, off the
    /// worker thread so the loop keeps draining. Cancellation-aware: a shutdown during the delay
    /// silently drops the retry.
    /// </summary>
    private void ScheduleReconcileAllRetry(int attempt, CancellationToken loopShutdownToken)
    {
        logger?.LogInformation(
            "Scheduling ReconcileAll retry {Attempt}/{Max} in {Delay}s",
            attempt, MaxReconcileAllRetries, _reconcileAllRetryDelay.TotalSeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_reconcileAllRetryDelay, loopShutdownToken);
                _jobs.Writer.TryWrite(new ReconcileAllJob(attempt));
            }
            catch (OperationCanceledException)
            {
                // Shutting down — drop the retry.
            }
        }, loopShutdownToken);
    }

    /// <summary>
    /// Reconciles every SingleKey wallet known to storage. Per-wallet failures during a
    /// good-backend pass are absorbed and logged so one bad wallet doesn't abort the whole pass.
    /// <para>
    /// First probes backend availability via <see cref="IClientTransport.GetServerInfoAsync"/>.
    /// If the backend is unreachable (e.g. arkd down at boot after a rotation-while-offline) the
    /// probe throws and the WHOLE pass fails — surfaced to the loop so the bounded retry requeues
    /// it. Without the probe, "arkd down" would only surface inside each wallet's
    /// <see cref="ISingleKeyDefaultEnsurer.EnsureDefaultAsync"/> (its own GetServerInfo call),
    /// which the per-wallet catch below absorbs, so the pass would silently reconcile nothing and
    /// never retry. The probe is a cheap availability gate: a successful result is cached by the
    /// transport, so the per-wallet ensures reuse it rather than re-fetching.
    /// </para>
    /// </summary>
    public async Task ReconcileAllAsync(CancellationToken cancellationToken = default)
    {
        // Availability probe: if the backend is unreachable this throws and aborts the whole pass
        // (the loop's bounded retry will requeue it). A good-backend pass proceeds and the cached
        // server info is reused by each wallet's EnsureDefaultAsync.
        await clientTransport.GetServerInfoAsync(cancellationToken);

        var wallets = await walletStorage.LoadAllWallets(cancellationToken);
        foreach (var wallet in wallets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileWalletAsync(wallet.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                logger?.LogWarning(0, e, "Reconciliation failed for wallet {WalletId}; continuing", wallet.Id);
            }
        }
    }

    /// <summary>
    /// Ensures the wallet's current-signer Default exists, then deactivates any stale
    /// pre-rotation defaults (Active, <c>Source="Default"</c>, script != current). No-op when
    /// the wallet is missing or not <see cref="WalletType.SingleKey"/>.
    /// </summary>
    public async Task ReconcileWalletAsync(string walletId, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken);
        if (wallet is null)
        {
            logger?.LogDebug("Reconciliation skipped: wallet {WalletId} not found", walletId);
            return;
        }

        // Destination-safety check: runs for any wallet type that has an Arkade destination.
        ArkAddress.TryParse(wallet.Destination ?? string.Empty, out var dest);
        if (dest is not null)
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
            var isStale = DestinationSafety.IsStale(dest, serverInfo);
            var alreadyFlagged = wallet.Metadata?.ContainsKey(DestinationSafety.PendingConfirmationMetadataKey) == true;

            if (isStale && !alreadyFlagged)
            {
                var deprecatedHex = Convert.ToHexString(dest.ServerKey.ToBytes()).ToLowerInvariant();
                await walletStorage.SetMetadataValue(
                    walletId, DestinationSafety.PendingConfirmationMetadataKey, deprecatedHex, cancellationToken);
                DestinationDisabled?.Invoke(this, new DestinationDisabledEventArgs
                {
                    WalletId = wallet.Id,
                    Destination = wallet.Destination!,
                    DeprecatedServerKey = deprecatedHex,
                });
                logger?.LogWarning(
                    "Arkade sweep destination for wallet {WalletId} is stale (deprecated server key {Key}); destination disabled pending re-confirmation",
                    walletId, deprecatedHex);
            }
            else if (!isStale && alreadyFlagged)
            {
                await walletStorage.SetMetadataValue(
                    walletId, DestinationSafety.PendingConfirmationMetadataKey, null, cancellationToken);
                logger?.LogInformation(
                    "Arkade sweep destination for wallet {WalletId} is no longer stale; cleared pending-confirmation flag",
                    walletId);
            }
            // stale && alreadyFlagged → no-op (no duplicate event, no redundant write)
        }

        if (wallet.WalletType != WalletType.SingleKey)
            return;

        // currentScript is now always the ArkPaymentContract default script (C1 fix:
        // EnsureDefaultAsync builds it directly and never returns a sweep-destination script),
        // so the supersede loop below deactivates only stale Source="Default" rows.
        var currentScript = await defaultEnsurer.EnsureDefaultAsync(walletId, cancellationToken);

        var activeContracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            cancellationToken: cancellationToken);

        foreach (var contract in activeContracts)
        {
            if (contract.Metadata is null
                || !contract.Metadata.TryGetValue(SourceMetadataKey, out var source)
                || source != DefaultSourceValue)
            {
                continue;
            }
            if (contract.Script == currentScript)
                continue;

            logger?.LogInformation(
                "Superseding stale default contract {Script} for wallet {WalletId} (current default {CurrentScript})",
                contract.Script, walletId, currentScript);
            await contractStorage.UpdateContractActivityState(
                walletId, contract.Script, ContractActivityState.Inactive, cancellationToken);
        }
    }

    private void OnWalletSaved(object? sender, ArkWalletInfo wallet)
    {
        // Enqueue for SingleKey wallets (default-contract reconciliation) and for any
        // wallet with a non-empty Destination (destination-safety re-check on re-save).
        if (wallet.WalletType != WalletType.SingleKey && string.IsNullOrEmpty(wallet.Destination))
            return;
        _jobs.Writer.TryWrite(new ReconcileWalletJob(wallet.Id));
    }

    private void OnServerInfoChanged(object? sender, ServerInfoChangedEventArgs e) =>
        _jobs.Writer.TryWrite(new ReconcileAllJob());

    public async ValueTask DisposeAsync()
    {
        logger?.LogDebug("Disposing contract reconciliation service");
        walletStorage.WalletSaved -= OnWalletSaved;
        serverInfoCacheInvalidation.ServerInfoChanged -= OnServerInfoChanged;

        try
        {
            await _shutdownCts.CancelAsync();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Error cancelling shutdown token during reconciliation service shutdown");
        }

        try
        {
            if (_workerTask is not null)
                await _workerTask;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Reconciliation worker completed with error during shutdown");
        }

        _multiToken?.Dispose();
        _shutdownCts.Dispose();
        logger?.LogInformation("Contract reconciliation service disposed");
    }
}

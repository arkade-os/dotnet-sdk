using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
    ILogger<ContractReconciliationService>? logger = null) : IAsyncDisposable
{
    private const string SourceMetadataKey = "Source";
    private const string DefaultSourceValue = "Default";

    private abstract record ReconcileJob;
    private sealed record ReconcileWalletJob(string WalletId) : ReconcileJob;
    private sealed record ReconcileAllJob : ReconcileJob;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<ReconcileJob> _jobs = Channel.CreateUnbounded<ReconcileJob>();

    private Task? _workerTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogInformation("Starting contract reconciliation service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _workerTask = DoReconciliationLoop(multiToken.Token);

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
                logger?.LogInformation(0, e, "Error during reconciliation loop execution for job {JobType}", job.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Reconciles every SingleKey wallet known to storage. Per-wallet failures are absorbed
    /// and logged so one bad wallet doesn't abort the whole pass.
    /// </summary>
    public async Task ReconcileAllAsync(CancellationToken cancellationToken = default)
    {
        var wallets = await walletStorage.LoadAllWallets(cancellationToken);
        foreach (var wallet in wallets)
        {
            if (wallet.WalletType != WalletType.SingleKey)
                continue;
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
        if (wallet.WalletType != WalletType.SingleKey)
            return;

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
        // Cheap pre-filter so we don't enqueue work for HD wallets; the worker re-checks.
        if (wallet.WalletType != WalletType.SingleKey)
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

        _shutdownCts.Dispose();
        logger?.LogInformation("Contract reconciliation service disposed");
    }
}

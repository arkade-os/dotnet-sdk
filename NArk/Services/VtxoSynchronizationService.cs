using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Transport;

namespace NArk.Services;

public class VtxoSynchronizationService : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _queryTask;

    private CancellationTokenSource? _restartCts;
    private Task? _streamTask;

    private HashSet<string> _lastViewOfScripts = [];

    private readonly SemaphoreSlim _viewSyncLock = new(1);

    private readonly Channel<HashSet<string>> _readyToPoll =
        Channel.CreateBounded<HashSet<string>>(new BoundedChannelOptions(5));

    private readonly IVtxoStorage _vtxoStorage;
    private readonly IClientTransport _arkClientTransport;
    private readonly IEnumerable<IActiveScriptsProvider> _activeScriptsProviders;
    private readonly ILogger<VtxoSynchronizationService>? _logger;

    public VtxoSynchronizationService(
        IEnumerable<IActiveScriptsProvider> activeScriptsProviders,
        IVtxoStorage vtxoStorage,
        IClientTransport arkClientTransport,
        ILogger<VtxoSynchronizationService> logger)
        : this(vtxoStorage, arkClientTransport, activeScriptsProviders)
    {
        _logger = logger;
    }

    public VtxoSynchronizationService(
        IVtxoStorage vtxoStorage,
        IClientTransport arkClientTransport,
        IEnumerable<IActiveScriptsProvider> activeScriptsProviders)
    {
        _vtxoStorage = vtxoStorage;
        _arkClientTransport = arkClientTransport;
        _activeScriptsProviders = activeScriptsProviders;

        foreach (var provider in _activeScriptsProviders)
        {
            provider.ActiveScriptsChanged += OnActiveScriptsChanged;
        }

        // Subscribe to VTXO changes for auto-deactivation of awaiting contracts
        _vtxoStorage.VtxosChanged += OnVtxoReceived;
    }

    private async void OnVtxoReceived(object? sender, ArkVtxo vtxo)
    {
        try
        {
            await DeactivateAwaitingContractsForScript(vtxo.Script);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Error auto-deactivating contracts for script {Script}", vtxo.Script);
        }
    }

    private async Task DeactivateAwaitingContractsForScript(string script)
    {
        // Find all contract storages and deactivate awaiting contracts
        foreach (var provider in _activeScriptsProviders)
        {
            if (provider is IContractStorage contractStorage)
            {
                var count = await contractStorage.DeactivateAwaitingContractsByScript(script, _shutdownCts.Token);
                if (count > 0)
                {
                    _logger?.LogInformation("Auto-deactivated {Count} awaiting contracts for script {Script}", count, script);
                }
            }
        }
    }

    private async void OnActiveScriptsChanged(object? sender, EventArgs e)
    {
        try
        {
            await UpdateScriptsView(_shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            var senderStr = sender?.GetType().Name ?? "";
            _logger?.LogDebug($"Active Script handler {senderStr} cancelled");
        }
        catch (Exception ex)
        {
            var senderStr = sender?.GetType().Name ?? "";
            _logger?.LogWarning(0, ex, $"Error handling active scripts change event from {senderStr}");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting VTXO synchronization service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        _queryTask = StartQueryLogic(multiToken.Token);
        await UpdateScriptsView(multiToken.Token);
    }

    private async Task UpdateScriptsView(CancellationToken token)
    {
        await _viewSyncLock.WaitAsync(token);
        try
        {
            var newViewOfScripts = (await Task.WhenAll(_activeScriptsProviders.Select(p => p.GetActiveScripts(token)))).SelectMany(c => c).ToHashSet();

            if (newViewOfScripts.Count == 0)
                return;

            // We already have a stream with this exact script list
            if (newViewOfScripts.SetEquals(_lastViewOfScripts) && _streamTask is not null && !_streamTask.IsCompleted)
            {
                _logger?.LogDebug("Scripts view unchanged, skipping stream restart");
                return;
            }

            try
            {
                if (_restartCts is not null)
                    await _restartCts.CancelAsync();
                if (_streamTask is not null)
                    await _streamTask;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(0, ex, "Error cancelling previous stream during scripts view update");
            }

            _lastViewOfScripts = newViewOfScripts;
            _restartCts = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            // Start a new subscription stream
            _streamTask = StartStreamLogic(newViewOfScripts, _restartCts.Token);
            // Do an initial poll of all scripts
            await _readyToPoll.Writer.WriteAsync(newViewOfScripts, token);
        }
        finally
        {
            _viewSyncLock.Release();
        }
    }

    private async Task StartStreamLogic(HashSet<string> scripts, CancellationToken token)
    {
        _logger?.LogDebug("Starting stream logic for {ScriptCount} scripts", scripts.Count);
        try
        {
            var restartableToken =
                CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            await foreach (var vtxosToPoll in _arkClientTransport.GetVtxoToPollAsStream(scripts, restartableToken.Token))
            {
                await _readyToPoll.Writer.WriteAsync(vtxosToPoll, restartableToken.Token);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger?.LogWarning(0, ex, "Stream logic failed, restarting scripts view");
            await UpdateScriptsView(_shutdownCts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(0, ex, "Stream logic cancelled");
        }
    }

    private async Task? StartQueryLogic(CancellationToken cancellationToken)
    {
        await foreach (var pollBatch in _readyToPoll.Reader.ReadAllAsync(cancellationToken))
        {
            await foreach (var vtxo in _arkClientTransport.GetVtxoByScriptsAsSnapshot(pollBatch, cancellationToken))
            {
                // Upsert
                var updated = await _vtxoStorage.UpsertVtxo(vtxo, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger?.LogDebug("Disposing VTXO synchronization service");
        await _shutdownCts.CancelAsync();

        _vtxoStorage.VtxosChanged -= OnVtxoReceived;

        foreach (var provider in _activeScriptsProviders)
        {
            provider.ActiveScriptsChanged -= OnActiveScriptsChanged;
        }
        try
        {
            if (_queryTask is not null)
                await _queryTask;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Query task cancelled during disposal");
        }
        try
        {
            if (_streamTask is not null)
                await _streamTask;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Stream task cancelled during disposal");
        }

        _logger?.LogInformation("VTXO synchronization service disposed");
    }
}
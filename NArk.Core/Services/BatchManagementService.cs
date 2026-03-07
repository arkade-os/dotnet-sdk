using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Batches;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Helpers;
using NArk.Core.Models;
using NArk.Core.Transport;
using NArk.Core.Extensions;
using NBitcoin.Crypto;

namespace NArk.Core.Services;

/// <summary>
/// Service for managing Ark intents with automatic submission, event monitoring, and batch participation.
/// Uses a single persistent gRPC event stream with dynamic topic updates via UpdateStreamTopics.
/// </summary>
public class BatchManagementService(
    IIntentStorage intentStorage,
    IClientTransport clientTransport,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IWalletProvider walletProvider,
    ICoinService coinService,
    ISafetyService safetyService,
    IEnumerable<IEventHandler<PostBatchSessionEvent>> eventHandlers,
    ILogger<BatchManagementService>? logger = null)
    : IAsyncDisposable
{
    private record Connection(
        Task ConnectionTask,
        CancellationTokenSource CancellationTokenSource
    );

    private static readonly TimeSpan EventStreamRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, ArkIntent> _activeIntents = new();
    private readonly ConcurrentDictionary<string, BatchSession> _activeBatchSessions = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _batchIdToIntentIds = new();

    private string? _streamId;
    private Connection? _streamConnection;
    private readonly SemaphoreSlim _topicUpdateSemaphore = new(1, 1);

    private CancellationTokenSource? _serviceCts;
    private bool _disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = new CancellationTokenSource();
        await LoadActiveIntentsAsync(cancellationToken);

        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts.Token);
        _streamConnection = new Connection(
            RunSingleEventStreamAsync(streamCts.Token),
            streamCts
        );

        intentStorage.IntentChanged += OnIntentChanged;
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        if (intent.State == ArkIntentState.WaitingForBatch && intent.IntentId is not null)
        {
            if (_activeIntents.TryAdd(intent.IntentId, intent))
            {
                var topics = GetTopicsForIntent(intent);
                _ = UpdateTopicsAsync(addTopics: topics);
            }
        }
        else if (intent.State is ArkIntentState.Cancelled or ArkIntentState.BatchFailed or ArkIntentState.BatchSucceeded)
        {
            if (intent.IntentId is not null && _activeIntents.TryRemove(intent.IntentId, out var removed))
            {
                var topics = GetTopicsForIntent(removed);
                _ = UpdateTopicsAsync(removeTopics: topics);
            }
        }
    }

    private async Task UpdateTopicsAsync(string[]? addTopics = null, string[]? removeTopics = null)
    {
        await _topicUpdateSemaphore.WaitAsync();
        try
        {
            if (_streamId is null)
            {
                logger?.LogDebug("Stream not yet started, skipping topic update");
                return;
            }

            await clientTransport.UpdateStreamTopicsAsync(_streamId, addTopics, removeTopics);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to update stream topics");
        }
        finally
        {
            _topicUpdateSemaphore.Release();
        }
    }

    private async Task RunSingleEventStreamAsync(CancellationToken cancellationToken)
    {
        logger?.LogDebug("BatchManagementService: Single event stream starting");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _streamId = null;
                var topics = GetAllTopics();

                // Even with no topics, open the stream so we can add topics dynamically
                // via UpdateStreamTopics when new intents arrive

                await foreach (var eventResponse in clientTransport.GetEventStreamAsync(
                                   new GetEventStreamRequest(topics), cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessEventAsync(eventResponse, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested ||
                                       ex.InnerException is OperationCanceledException)
            {
                // Cancellation during gRPC call
            }
            catch (Exception ex)
            {
                logger?.LogError(0, ex, "Error in event stream, restarting in {Seconds} seconds",
                    EventStreamRetryDelay.TotalSeconds);
                _streamId = null;
                await Task.Delay(EventStreamRetryDelay, cancellationToken);
            }
        }
    }

    private async Task ProcessEventAsync(BatchEvent eventResponse, CancellationToken cancellationToken)
    {
        switch (eventResponse)
        {
            case StreamStartedEvent streamStarted:
                _streamId = streamStarted.StreamId;
                logger?.LogDebug("Event stream started with ID {StreamId}", _streamId);
                break;

            case BatchStartedEvent batchStarted:
                await HandleBatchStartedForAllIntentsAsync(batchStarted, CancellationToken.None);
                break;

            // Route batch-specific events to the appropriate session(s)
            case BatchFailedEvent or BatchFinalizedEvent or BatchFinalizationEvent
                or TreeTxEvent or TreeSigningStartedEvent or TreeNoncesEvent
                or TreeNoncesAggregatedEvent or TreeSignatureEvent:
            {
                var batchId = GetBatchId(eventResponse);
                if (batchId is not null)
                    await RouteToBatchSessionsAsync(batchId, eventResponse, cancellationToken);
                break;
            }
        }
    }

    private static string? GetBatchId(BatchEvent evt) => evt switch
    {
        TreeTxEvent e => e.Id,
        TreeSigningStartedEvent e => e.Id,
        TreeNoncesEvent e => e.Id,
        TreeNoncesAggregatedEvent e => e.Id,
        TreeSignatureEvent e => e.Id,
        BatchFinalizationEvent e => e.Id,
        BatchFinalizedEvent e => e.Id,
        BatchFailedEvent e => e.Id,
        _ => null
    };

    private async Task RouteToBatchSessionsAsync(string batchId, BatchEvent eventResponse,
        CancellationToken cancellationToken)
    {
        if (!_batchIdToIntentIds.TryGetValue(batchId, out var intentIds))
            return;

        string[] ids;
        lock (intentIds)
        {
            ids = intentIds.ToArray();
        }

        foreach (var intentId in ids)
        {
            if (!_activeBatchSessions.TryGetValue(intentId, out var session))
                continue;

            if (!_activeIntents.TryGetValue(intentId, out var intent))
                continue;

            try
            {
                var isComplete = await session.ProcessEventAsync(eventResponse, cancellationToken);
                if (isComplete)
                {
                    CleanupBatchSession(intentId, batchId);
                }

                switch (eventResponse)
                {
                    case BatchFailedEvent batchFailed when batchFailed.Id == intent.BatchId:
                        await HandleBatchFailedAsync(intent, batchFailed, cancellationToken);
                        CleanupBatchSession(intentId, batchId);
                        _activeIntents.TryRemove(intentId, out _);
                        _ = UpdateTopicsAsync(removeTopics: GetTopicsForIntent(intent));
                        break;

                    case BatchFinalizedEvent batchFinalized when batchFinalized.Id == intent.BatchId:
                        await HandleBatchFinalizedAsync(intent, batchFinalized, cancellationToken);
                        CleanupBatchSession(intentId, batchId);
                        _activeIntents.TryRemove(intentId, out _);
                        break;
                }
            }
            catch (Exception ex)
            {
                await HandleBatchExceptionAsync(intent, ex, cancellationToken);
                CleanupBatchSession(intentId, batchId);
            }
        }
    }

    private void CleanupBatchSession(string intentId, string batchId)
    {
        _activeBatchSessions.TryRemove(intentId, out _);

        if (_batchIdToIntentIds.TryGetValue(batchId, out var intentIds))
        {
            lock (intentIds)
            {
                intentIds.Remove(intentId);
                if (intentIds.Count == 0)
                    _batchIdToIntentIds.TryRemove(batchId, out _);
            }
        }
    }

    #region Private Methods

    private async Task LoadActiveIntentsAsync(CancellationToken cancellationToken, bool firstRun = true)
    {
        var activeStates = new[] { ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch, ArkIntentState.BatchInProgress };
        var allActiveIntents = await intentStorage.GetIntents(states: activeStates, cancellationToken: cancellationToken);

        // Group intents by their VTXOs to detect duplicates
        var vtxoToIntents = new Dictionary<string, List<ArkIntent>>();
        foreach (var intent in allActiveIntents)
        {
            foreach (var vtxo in intent.IntentVtxos)
            {
                var key = $"{vtxo.Hash}:{vtxo.N}";
                if (!vtxoToIntents.ContainsKey(key))
                    vtxoToIntents[key] = [];
                vtxoToIntents[key].Add(intent);
            }
        }

        // Find VTXOs that have multiple intents - these are duplicates that need cleanup
        var duplicateVtxos = vtxoToIntents.Where(kv => kv.Value.Count > 1).ToList();
        if (duplicateVtxos.Any())
        {
            logger?.LogWarning(
                "Found {Count} VTXOs with multiple active intents - cleaning up duplicates",
                duplicateVtxos.Count);

            var intentsToCancel = new HashSet<string>();
            foreach (var (vtxoKey, intents) in duplicateVtxos)
            {
                var sorted = intents.OrderByDescending(i => i.UpdatedAt).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    intentsToCancel.Add(sorted[i].IntentTxId);
                }
            }

            foreach (var intentTxId in intentsToCancel)
            {
                var intent = allActiveIntents.First(i => i.IntentTxId == intentTxId);
                logger?.LogWarning(
                    "Cancelling duplicate intent {IntentTxId} (IntentId: {IntentId}) - VTXO already claimed by another intent",
                    intent.IntentTxId, intent.IntentId);

                var cancelledIntent = intent with
                {
                    State = ArkIntentState.Cancelled,
                    CancellationReason = "Duplicate intent for same VTXO - cleaned up on startup",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await intentStorage.SaveIntent(cancelledIntent.WalletId, cancelledIntent, cancellationToken);
            }

            allActiveIntents = allActiveIntents.Where(i => !intentsToCancel.Contains(i.IntentTxId)).ToList();
        }

        foreach (var intent in allActiveIntents)
        {
            if (intent.IntentId is null)
            {
                logger?.LogDebug("Skipping intent with null IntentId (IntentTxId: {IntentTxId})", intent.IntentTxId);
                continue;
            }

            if (firstRun && intent.State == ArkIntentState.BatchInProgress)
            {
                if (intent.CommitmentTransactionId is not null)
                {
                    logger?.LogInformation(
                        "Orphaned BatchInProgress intent {IntentId} has commitment tx {CommitmentTx} — marking as succeeded",
                        intent.IntentId, intent.CommitmentTransactionId);

                    var succeededIntent = intent with
                    {
                        State = ArkIntentState.BatchSucceeded,
                        CancellationReason = null,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await intentStorage.SaveIntent(succeededIntent.WalletId, succeededIntent, cancellationToken);
                    continue;
                }

                logger?.LogWarning(
                    "Cancelling orphaned BatchInProgress intent {IntentId} on startup (no active batch session)",
                    intent.IntentId);

                var cancelledIntent = intent with
                {
                    State = ArkIntentState.Cancelled,
                    CancellationReason = "Orphaned BatchInProgress - no active batch session after restart",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await intentStorage.SaveIntent(cancelledIntent.WalletId, cancelledIntent, cancellationToken);
                continue;
            }

            logger?.LogDebug("Loaded active intent {IntentId} in state {State}", intent.IntentId, intent.State);
            _activeIntents[intent.IntentId] = intent;
        }
    }

    private async Task SaveToStorage(string intentId, Func<ArkIntent?, ArkIntent> updateFunc,
        CancellationToken cancellationToken = default)
    {
        var newValue = _activeIntents.AddOrUpdate(intentId, _ => updateFunc(null), (_, old) => updateFunc(old));
        await intentStorage.SaveIntent(newValue.WalletId, newValue, cancellationToken);
    }

    private async Task HandleBatchExceptionAsync(ArkIntent intent, Exception ex, CancellationToken cancellationToken)
    {
        await SaveToStorage(intent.IntentId!, GetNewIntent, cancellationToken);

        await eventHandlers.SafeHandleEventAsync(
            new PostBatchSessionEvent(intent, null, ActionState.Failed, $"Exception: {ex}"),
            cancellationToken);
        return;

        ArkIntent GetNewIntent(ArkIntent? arg)
        {
            if (arg is null) throw new InvalidOperationException("Intent was not found in cache");
            if (arg.State is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed)
                return arg;
            return arg with
            {
                State = ArkIntentState.BatchFailed,
                CancellationReason = $"Batch failed: {ex}",
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private async Task HandleBatchStartedForAllIntentsAsync(
        BatchStartedEvent batchEvent,
        CancellationToken cancellationToken)
    {
        var intentHashMap = new Dictionary<string, string>();
        foreach (var (intentId, _) in _activeIntents)
        {
            var intentIdBytes = Encoding.UTF8.GetBytes(intentId);
            var intentIdHash = Hashes.SHA256(intentIdBytes);
            var intentIdHashStr = intentIdHash.ToHexStringLower();
            intentHashMap[intentIdHashStr] = intentId;
        }

        var selectedIntentIds = new List<string>();
        foreach (var intentIdHash in batchEvent.IntentIdHashes)
        {
            if (intentHashMap.TryGetValue(intentIdHash, out var intentId))
            {
                selectedIntentIds.Add(intentId);
            }
        }

        if (selectedIntentIds.Count == 0)
            return;

        var walletIds = selectedIntentIds
            .Select(id => _activeIntents.TryGetValue(id, out var intent) ? intent.WalletId : null)
            .Where(wid => wid != null)
            .Select(wid => wid!)
            .Distinct()
            .ToArray();

        if (walletIds.Length == 0)
            return;

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        foreach (var intentId in selectedIntentIds)
        {
            if (!_activeIntents.TryGetValue(intentId, out var intent) || _activeBatchSessions.ContainsKey(intentId))
                continue;

            try
            {
                await SetupBatchSessionAsync(intentId, intent, serverInfo, batchEvent, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(0, ex, "Failed to handle batch started event for intent {IntentId}", intentId);
            }
        }
    }

    private async Task SetupBatchSessionAsync(string intentId, ArkIntent intent, ArkServerInfo serverInfo,
        BatchStartedEvent batchEvent, CancellationToken cancellationToken)
    {
        logger?.LogInformation("BatchManagementService: setting up batch session for intent {IntentId}", intentId);

        try
        {
            HashSet<ArkCoin> spendableCoins = [];
            var vtxos = await vtxoStorage.GetVtxos(
                outpoints: intent.IntentVtxos,
                includeSpent: true,
                walletIds: [intent.WalletId],
                cancellationToken: cancellationToken);
            var vtxoScripts = vtxos.Select(v => v.Script).ToHashSet();
            var contracts = await contractStorage.GetContracts(
                scripts: vtxoScripts.ToArray(),
                walletIds: [intent.WalletId],
                cancellationToken: cancellationToken);
            foreach (var outpoint in intent.IntentVtxos)
            {
                var vtxo = vtxos.FirstOrDefault(v => v.OutPoint == outpoint);
                if (vtxo is null)
                {
                    logger?.LogWarning("VTXO {Outpoint} not found in storage for intent {IntentId}", outpoint,
                        intentId);
                    throw new InvalidOperationException(
                        $"VTXO {outpoint} not found in storage for intent {intentId}");
                }
                var contract = contracts.FirstOrDefault(c => c.Script == vtxo.Script);
                if (contract is null)
                {
                    logger?.LogWarning("Contract for VTXO {Outpoint} not found in storage for intent {IntentId}",
                        outpoint, intentId);
                    throw new InvalidOperationException(
                        $"Contract for VTXO {outpoint} not found in storage for intent {intentId}");
                }
                spendableCoins.Add(
                    await coinService.GetCoin(contract, vtxo, cancellationToken)
                );
            }

            var session = new BatchSession(
                clientTransport,
                walletProvider,
                new TransactionHelpers.ArkTransactionBuilder(clientTransport, safetyService, walletProvider,
                    intentStorage),
                serverInfo.Network,
                intent,
                spendableCoins.ToArray(),
                batchEvent,
                logger);

            await session.InitializeAsync(cancellationToken);

            // Register session before confirming — events arrive on the single stream immediately
            _activeBatchSessions[intentId] = session;

            var batchIntentIds = _batchIdToIntentIds.GetOrAdd(batchEvent.Id, _ => new HashSet<string>());
            lock (batchIntentIds)
            {
                batchIntentIds.Add(intentId);
            }

            try
            {
                await clientTransport.ConfirmRegistrationAsync(intentId, cancellationToken: cancellationToken);

                await SaveToStorage(intentId, arkIntent =>
                    (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
                    {
                        BatchId = batchEvent.Id,
                        State = ArkIntentState.BatchInProgress,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, cancellationToken);
            }
            catch
            {
                CleanupBatchSession(intentId, batchEvent.Id);
                throw;
            }
        }
        catch (Exception ex)
        {
            await HandleBatchExceptionAsync(intent, ex, cancellationToken);
        }
    }

    private static IEnumerable<string> ExtractCosignerKeys(string registerProofMessage)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Messages.RegisterIntentMessage>(registerProofMessage);
            return message?.CosignersPublicKeys ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string[] GetTopicsForIntent(ArkIntent intent)
    {
        var vtxoTopics = intent.IntentVtxos
            .Select(iv => $"{iv.Hash}:{iv.N}");
        var cosignerTopics = ExtractCosignerKeys(intent.RegisterProofMessage);
        return vtxoTopics.Concat(cosignerTopics).ToArray();
    }

    private string[] GetAllTopics()
    {
        return _activeIntents.Values
            .SelectMany(GetTopicsForIntent)
            .Distinct()
            .ToArray();
    }

    private async Task HandleBatchFailedAsync(
        ArkIntent intent,
        BatchFailedEvent batchEvent,
        CancellationToken cancellationToken)
    {
        var reason = !string.IsNullOrEmpty(batchEvent.Reason)
            ? $"Batch failed: {batchEvent.Reason}"
            : "Batch failed";

        await SaveToStorage(intent.IntentId!, arkIntent =>
            (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
            {
                State = ArkIntentState.BatchFailed,
                CancellationReason = reason,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

        await eventHandlers.SafeHandleEventAsync(
            new PostBatchSessionEvent(intent, null, ActionState.Failed, reason),
            cancellationToken);
    }

    private async Task HandleBatchFinalizedAsync(
        ArkIntent intent,
        BatchFinalizedEvent finalizedEvent,
        CancellationToken cancellationToken)
    {
        await SaveToStorage(intent.IntentId!, arkIntent =>
            (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
            {
                State = ArkIntentState.BatchSucceeded,
                CancellationReason = null,
                CommitmentTransactionId = finalizedEvent.CommitmentTxId,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

        await eventHandlers.SafeHandleEventAsync(
            new PostBatchSessionEvent(intent, finalizedEvent.CommitmentTxId, ActionState.Successful, null),
            cancellationToken);
    }

    #endregion


    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        intentStorage.IntentChanged -= OnIntentChanged;

        try
        {
            if (_serviceCts is not null)
                await _serviceCts.CancelAsync();
        }
        catch (ObjectDisposedException ex)
        {
            logger?.LogDebug(0, ex, "Service CancellationTokenSource already disposed during cleanup");
        }

        try
        {
            if (_streamConnection is not null)
            {
                try { await _streamConnection.CancellationTokenSource.CancelAsync(); }
                catch (ObjectDisposedException) { }

                try { await _streamConnection.ConnectionTask; }
                catch (Exception ex) { logger?.LogDebug(0, ex, "Stream task completed with error during disposal"); }

                try { _streamConnection.CancellationTokenSource.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Error during stream connection cleanup");
        }

        try
        {
            _topicUpdateSemaphore.Dispose();
        }
        catch (ObjectDisposedException ex)
        {
            logger?.LogDebug(0, ex, "Topic update semaphore already disposed during cleanup");
        }

        _serviceCts?.Dispose();

        _activeIntents.Clear();
        _activeBatchSessions.Clear();
        _batchIdToIntentIds.Clear();

        _disposed = true;
    }

    public BatchManagementService(IIntentStorage intentStorage,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        IWalletProvider walletProvider,
        ICoinService coinService,
        ISafetyService safetyService)
        : this(intentStorage, clientTransport, vtxoStorage, contractStorage, walletProvider, coinService, safetyService, [])
    {
    }

    public BatchManagementService(IIntentStorage intentStorage,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        IWalletProvider walletProvider,
        ICoinService coinService,
        ISafetyService safetyService,
        ILogger<BatchManagementService> logger)
        : this(intentStorage, clientTransport, vtxoStorage, contractStorage, walletProvider, coinService, safetyService, [], logger)
    {
    }
}

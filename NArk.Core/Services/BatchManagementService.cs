using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
using NBitcoin;
using NBitcoin.Crypto;

namespace NArk.Core.Services;

/// <summary>
/// Service for managing Ark intents with automatic submission, event monitoring, and batch participation
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
    private record BatchSessionWithConnection(
        Connection Connection,
        BatchSession BatchSession
    );

    private record Connection(
        Task ConnectionTask,
        CancellationTokenSource CancellationTokenSource
    );

    // Polling intervals
    private static readonly TimeSpan EventStreamRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, ArkIntent> _activeIntents = new();
    private readonly ConcurrentDictionary<string, BatchSessionWithConnection> _activeBatchSessions = new();

    private Connection? _sharedMainConnection;
    private readonly SemaphoreSlim _connectionManipulationSemaphore = new(1, 1);

    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    private CancellationTokenSource? _serviceCts;
    private bool _disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = new CancellationTokenSource();
        // Load existing WaitingForBatch intents and start a shared event stream
        await LoadActiveIntentsAsync(cancellationToken);
        _ = RunSharedEventStreamController(_serviceCts.Token);
        await _triggerChannel.Writer.WriteAsync("STARTUP", cancellationToken);
        intentStorage.IntentChanged += OnIntentChanged;
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        // Only trigger stream update if:
        // 1. A new intent is ready for batch (WaitingForBatch with IntentId)
        // 2. An intent was cancelled/completed and should be removed
        // Don't trigger for state changes within a batch (BatchInProgress updates)
        if (intent.State == ArkIntentState.WaitingForBatch && intent.IntentId is not null)
        {
            // New intent ready - check if we already know about it
            if (!_activeIntents.ContainsKey(intent.IntentId))
            {
                _triggerChannel.Writer.TryWrite("INTENT_ADDED");
            }
        }
        else if (intent.State is ArkIntentState.Cancelled or ArkIntentState.BatchFailed or ArkIntentState.BatchSucceeded)
        {
            // Intent completed - remove from tracking if present
            if (intent.IntentId is not null && _activeIntents.ContainsKey(intent.IntentId))
            {
                _triggerChannel.Writer.TryWrite("INTENT_COMPLETED");
            }
        }
        // Don't trigger for WaitingToSubmit (not yet ready) or BatchInProgress (already tracking)
    }

    private async Task RunSharedEventStreamController(CancellationToken cancellationToken)
    {
        await foreach (var triggerReason in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            logger?.LogDebug("Received trigger in EventStreamController: {TriggerReason}", triggerReason);
            await _connectionManipulationSemaphore.WaitAsync(cancellationToken);
            try
            {
                await LoadActiveIntentsAsync(cancellationToken);
                var cancellationTokenSourceForMain = new CancellationTokenSource();
                if (_sharedMainConnection is not null)
                    await _sharedMainConnection.CancellationTokenSource.CancelAsync();
                _sharedMainConnection = new Connection(
                    RunMainSharedEventStreamAsync(cancellationTokenSourceForMain.Token),
                    cancellationTokenSourceForMain
                );
            }
            finally
            {
                _connectionManipulationSemaphore.Release();
            }
        }
    }


    #region Private Methods

    private async Task LoadActiveIntentsAsync(CancellationToken cancellationToken)
    {
        var activeStates = new[] { ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch, ArkIntentState.BatchInProgress };
        var allActiveIntents = await intentStorage.GetIntents(states: activeStates, cancellationToken: cancellationToken);

        // Group intents by their VTXOs to detect duplicates
        // An intent's VTXOs are stored in IntentVtxos (list of outpoints)
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

            // For each VTXO with duplicates, keep only the most recent intent (by UpdatedAt)
            var intentsToCancel = new HashSet<string>();
            foreach (var (vtxoKey, intents) in duplicateVtxos)
            {
                // Sort by UpdatedAt descending, keep the first (most recent), cancel the rest
                var sorted = intents.OrderByDescending(i => i.UpdatedAt).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    intentsToCancel.Add(sorted[i].IntentTxId);
                }
            }

            // Cancel the duplicate intents
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

            // Remove cancelled intents from the list
            allActiveIntents = allActiveIntents.Where(i => !intentsToCancel.Contains(i.IntentTxId)).ToList();
        }

        foreach (var intent in allActiveIntents)
        {
            if (intent.IntentId is null)
            {
                logger?.LogDebug("Skipping intent with null IntentId (IntentTxId: {IntentTxId})", intent.IntentTxId);
                continue;
            }

            // Cancel all BatchInProgress intents on startup. Batch sessions are never carried
            // over across restarts, so any BatchInProgress intent is definitively stale.
            // The next generation cycle will create a fresh intent if needed.
            if (intent.State == ArkIntentState.BatchInProgress)
            {
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

    private async Task RunMainSharedEventStreamAsync(CancellationToken cancellationToken)
    {
        logger?.LogDebug("BatchManagementService: Main shared event stream started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Build topics from all active intents (VTXOs + cosigner public keys)
                var vtxoTopics = _activeIntents.Values
                    .SelectMany(intent => intent.IntentVtxos
                        .Select(iv => $"{iv.Hash}:{iv.N}"));

                var cosignerTopics = _activeIntents.Values
                    .SelectMany(intent => ExtractCosignerKeys(intent.RegisterProofMessage));

                var topics =
                    vtxoTopics.Concat(cosignerTopics).ToHashSet();

                // If we have no topic to listen for, jump out.
                if (topics.Count is 0) return;

                await foreach (var eventResponse in clientTransport.GetEventStreamAsync(
                                   new GetEventStreamRequest(topics.ToArray()), cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await ProcessSharedEventForAllIntentsAsync(eventResponse, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // ignored - normal shutdown
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested ||
                                       ex.InnerException is OperationCanceledException)
            {
                // Cancellation was requested or inner exception is cancellation (e.g., gRPC cancelled call)
                // This is expected during shutdown, don't log as error
            }
            catch (Exception ex)
            {
                logger?.LogError(0, ex, "Error in shared event stream, restarting in {Seconds} seconds",
                    EventStreamRetryDelay.TotalSeconds);
                await Task.Delay(EventStreamRetryDelay, cancellationToken);
            }
        }
    }

    private async Task ProcessSharedEventForAllIntentsAsync(BatchEvent eventResponse,
        CancellationToken cancellationToken)
    {
        // Handle BatchStarted event first - check all intents at once
        if (eventResponse is BatchStartedEvent batchStartedEvent)
        {
            await HandleBatchStartedForAllIntentsAsync(batchStartedEvent, cancellationToken);
        }
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

    private void TriggerStreamUpdate()
    {
        _triggerChannel.Writer.TryWrite("STREAM_UPDATE_REQUESTED");
    }

    private async Task HandleBatchStartedForAllIntentsAsync(
        BatchStartedEvent batchEvent,
        CancellationToken cancellationToken)
    {
        // Build a map of intent ID hashes to IDs for efficient lookup
        var intentHashMap = new Dictionary<string, string>();
        foreach (var (intentId, _) in _activeIntents)
        {
            var intentIdBytes = Encoding.UTF8.GetBytes(intentId);
            var intentIdHash = Hashes.SHA256(intentIdBytes);
            var intentIdHashStr = intentIdHash.ToHexStringLower();
            intentHashMap[intentIdHashStr] = intentId;
        }

        // Find all our intents that are included in this batch
        var selectedIntentIds = new List<string>();
        foreach (var intentIdHash in batchEvent.IntentIdHashes)
        {
            if (intentHashMap.TryGetValue(intentIdHash, out var intentId))
            {
                selectedIntentIds.Add(intentId);
            }
        }

        if (selectedIntentIds.Count == 0)
        {
            return; // None of our intents in this batch
        }

        // Load all VTXOs and contracts for selected intents in one efficient query
        var walletIds = selectedIntentIds
            .Select(id => _activeIntents.TryGetValue(id, out var intent) ? intent.WalletId : null)
            .Where(wid => wid != null)
            .Select(wid => wid!)
            .Distinct()
            .ToArray();

        if (walletIds.Length == 0)
        {
            return;
        }

        // Get spendable coins for all wallets, filtered by the specific VTXOs locked in intents

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Confirm registration and create batch sessions for all selected intents
        foreach (var intentId in selectedIntentIds)
        {
            if (!_activeIntents.TryGetValue(intentId, out var intent) || _activeBatchSessions.ContainsKey(intentId))
                continue;

            try
            {
                _ = RunConnectionForIntent(intentId, intent, serverInfo, batchEvent,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(0, ex, "Failed to handle batch started event for intent {IntentId}", intentId);
            }
        }
    }

    private async Task RunConnectionForIntent(string intentId, ArkIntent intent, ArkServerInfo serverInfo,
        BatchStartedEvent batchEvent,  CancellationToken cancellationToken)
    {
        logger?.LogInformation("BatchManagementService: start dedicated connection for intent {IntentId}", intentId);
        
        try
        {
            HashSet<ArkCoin> spendableCoins = [];
            var vtxos = await vtxoStorage.GetVtxos(
                outpoints:intent.IntentVtxos,
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
                    await coinService.GetCoin(contract,vtxo, cancellationToken)
                );
            }

            // Create and initialize a batch session
            var session = new BatchSession(
                clientTransport,
                walletProvider,
                new TransactionHelpers.ArkTransactionBuilder(clientTransport, safetyService, walletProvider,
                    intentStorage),
                serverInfo.Network,
                intent,
                spendableCoins.ToArray(),
                batchEvent);

            await session.InitializeAsync(cancellationToken);

            // Store the session so events can be passed to it

            await _connectionManipulationSemaphore.WaitAsync(cancellationToken);
            try
            {
                var sessionCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // Save state BEFORE starting HandleBatchEvents to ensure BatchId is available
                // when checking for BatchFailedEvent
                await SaveToStorage(intentId, arkIntent =>
                    (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
                    {
                        BatchId = batchEvent.Id,
                        State = ArkIntentState.BatchInProgress,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, sessionCancellationTokenSource.Token);

                _activeBatchSessions[intentId] = new BatchSessionWithConnection(
                    new Connection(
                        HandleBatchEvents(intentId, intent, session, sessionCancellationTokenSource.Token),
                        sessionCancellationTokenSource
                    ),
                    session
                );

                await clientTransport.ConfirmRegistrationAsync(
                    intentId,
                    cancellationToken: sessionCancellationTokenSource.Token);
            }
            finally
            {
                _connectionManipulationSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            await HandleBatchExceptionAsync(intent, ex, cancellationToken);
        }
    }

    private async Task HandleBatchEvents(string intentId, ArkIntent oldIntent, BatchSession session,
        CancellationToken cancellationToken)
    {
        // Build topics from all active intents (VTXOs + cosigner public keys)
        var vtxoTopics = oldIntent.IntentVtxos
            .Select(iv => $"{iv.Hash}:{iv.N}");

        var cosignerTopics = ExtractCosignerKeys(oldIntent.RegisterProofMessage);

        var topics =
            vtxoTopics.Concat(cosignerTopics).ToHashSet();


        await foreach (var eventResponse in clientTransport.GetEventStreamAsync(
                           new GetEventStreamRequest([..topics]), cancellationToken))
        {
            if (!_activeIntents.TryGetValue(intentId, out var intent))
                return;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isComplete =
                    await session.ProcessEventAsync(eventResponse, cancellationToken);
                if (isComplete)
                {
                    _activeBatchSessions.TryRemove(intentId, out _);
                    TriggerStreamUpdate();
                }

                // Handle events that affect this intent
                switch (eventResponse)
                {
                    case BatchFailedEvent batchFailedEvent:
                        if (batchFailedEvent.Id == intent.BatchId)
                        {
                            // Mark as BatchFailed and done - no auto-retry
                            // The VTXOs become available for new intents via Send flow or scheduler
                            await HandleBatchFailedAsync(intent, batchFailedEvent, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            TriggerStreamUpdate();
                        }

                        break;

                    case BatchFinalizedEvent batchFinalized:
                        if (batchFinalized.Id == intent.BatchId)
                        {
                            // Note: HandleBatchFinalizedAsync calls SaveToStorage which triggers OnIntentChanged.
                            // OnIntentChanged will write to _triggerChannel because the intent is still in
                            // _activeIntents at that point. We remove from _activeIntents AFTER to ensure
                            // the trigger is sent, then the trigger handler will reload and see the new state.
                            await HandleBatchFinalizedAsync(intent, batchFinalized, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            // Note: TriggerStreamUpdate() removed - OnIntentChanged handles this now
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                await HandleBatchExceptionAsync(oldIntent, ex, cancellationToken);
            }
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
            // If we can't parse the message, return empty
            return [];
        }
    }

    /// <summary>
    /// Handles a batch failure by marking the intent as failed.
    ///
    /// <para>The intent stays in <see cref="ArkIntentState.BatchFailed"/> state permanently.
    /// The VTXOs become available again for new intents via the Send flow or scheduler.</para>
    ///
    /// <para><b>Note:</b> We no longer auto-retry failed intents. If the user wants to retry,
    /// they can create a new intent which will automatically cancel this failed one.</para>
    /// </summary>
    private async Task HandleBatchFailedAsync(
        ArkIntent intent,
        BatchFailedEvent batchEvent,
        CancellationToken cancellationToken)
    {
        var reason = !string.IsNullOrEmpty(batchEvent.Reason)
            ? $"Batch failed: {batchEvent.Reason}"
            : "Batch failed";

        // Just mark as failed and done - no auto-retry
        // Keep BatchId for tracking/debugging
        await SaveToStorage(intent.IntentId!, arkIntent =>
            (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
            {
                State = ArkIntentState.BatchFailed,
                CancellationReason = reason,
                // Keep BatchId for tracking
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
                CancellationReason = null, // Clear any previous failure reason on success
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

        // Unsubscribe from intent changes first to prevent new triggers during disposal
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

        await _connectionManipulationSemaphore.WaitAsync();
        try
        {
            foreach (var (connection, _) in _activeBatchSessions.Values)
            {
                try
                {
                    await connection.CancellationTokenSource.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    await connection.ConnectionTask;
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(0, ex, "Session connection task completed with error during disposal");
                }

                try
                {
                    connection.CancellationTokenSource.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                if (_sharedMainConnection is not null)
                    await _sharedMainConnection.CancellationTokenSource.CancelAsync();
            }
            catch (ObjectDisposedException ex)
            {
                logger?.LogDebug(0, ex, "Main Connection CancellationTokenSource already disposed");
            }

            try
            {
                if (_sharedMainConnection is not null)
                    await _sharedMainConnection.ConnectionTask;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(0, ex, "Main task completed with error during disposal");
            }

            try
            {
                _sharedMainConnection?.CancellationTokenSource.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                logger?.LogDebug(0, ex,
                    "Main connection CancellationTokenSource already disposed during cleanup");
            }
        }
        finally
        {
            _connectionManipulationSemaphore.Release();
        }

        try
        {
            _connectionManipulationSemaphore.Dispose();
        }
        catch (ObjectDisposedException ex)
        {
            logger?.LogDebug(0, ex, "Connection manipulation semaphore already disposed during cleanup");
        }

        _serviceCts?.Dispose();

        _activeIntents.Clear();
        _activeBatchSessions.Clear();

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
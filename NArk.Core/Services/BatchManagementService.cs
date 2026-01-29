using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
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
        intentStorage.IntentChanged += (_, _) => _triggerChannel.Writer.TryWrite("INTENT_CHANGED");
    }

    private async Task RunSharedEventStreamController(CancellationToken cancellationToken)
    {
        await foreach (var triggerReason in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            logger?.LogInformation("Received trigger in EventStreamController: {TriggerReason}", triggerReason);
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
        foreach (var intent in await intentStorage.GetActiveIntents(cancellationToken))
        {
            if (intent.IntentId is null)
            {
                logger?.LogDebug("Skipping intent with null IntentId (IntentTxId: {IntentTxId})", intent.IntentTxId);
                continue;
            }

            logger?.LogInformation("Loaded active intent {IntentId} in state {State}", intent.IntentId, intent.State);
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
        logger?.LogInformation("BatchManagementService: Main shared event stream started");

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
                // ignored
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

        // Collect all VTXO outpoints from all selected intents
        var allVtxoOutpoints = selectedIntentIds
            .Where(id => _activeIntents.ContainsKey(id))
            .SelectMany(id => _activeIntents[id].IntentVtxos)
            .ToHashSet();

        // Get spendable coins for all wallets, filtered by the specific VTXOs locked in intents

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Confirm registration and create batch sessions for all selected intents
        foreach (var intentId in selectedIntentIds)
        {
            if (!_activeIntents.TryGetValue(intentId, out var intent) || _activeBatchSessions.ContainsKey(intentId))
                continue;

            try
            {
                _ = RunConnectionForIntent(intentId, intent, serverInfo, batchEvent, allVtxoOutpoints,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(0, ex, "Failed to handle batch started event for intent {IntentId}", intentId);
            }
        }
    }

    private async Task RunConnectionForIntent(string intentId, ArkIntent intent, ArkServerInfo serverInfo,
        BatchStartedEvent batchEvent, HashSet<OutPoint> allVtxoOutpoints, CancellationToken cancellationToken)
    {
        logger?.LogInformation("BatchManagementService: start dedicated connection for intent {IntentId}", intentId);

        try
        {
            HashSet<ArkCoin> allWalletCoins = [];
            foreach (var outpoint in allVtxoOutpoints)
            {
                var vtxo = (await vtxoStorage.GetVtxos(VtxoFilter.ByOutpoint(outpoint), cancellationToken)).FirstOrDefault()
                    ?? throw new InvalidOperationException("Unknown vtxo outpoint");
                allWalletCoins.Add(
                    await coinService.GetCoin(vtxo, intent.WalletId, cancellationToken)
                );
            }

            // Filter to only the VTXOs locked by this intent
            var intentVtxoOutpoints = intent.IntentVtxos.ToHashSet();

            var spendableCoins = allWalletCoins
                .Where(coin => intentVtxoOutpoints.Contains(coin.Outpoint))
                .ToList();

            if (spendableCoins.Count == 0)
            {
                logger?.LogWarning("No spendable coins found for intent {IntentId}", intentId);
                return;
            }

            await LoadActiveIntentsAsync(cancellationToken);

            // Create and initialize a batch session
            var session = new BatchSession(
                clientTransport,
                walletProvider,
                new TransactionHelpers.ArkTransactionBuilder(clientTransport, safetyService, walletProvider,
                    intentStorage),
                serverInfo.Network,
                intent,
                [.. spendableCoins],
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
                            await HandleBatchFailedAsync(intent, batchFailedEvent, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            // Remove from _activeIntents - the intent will be re-registered by
                            // IntentSynchronizationService with a new IntentId, and picked up
                            // again through the normal LoadActiveIntentsAsync flow
                            _activeIntents.TryRemove(intentId, out _);
                            TriggerStreamUpdate();
                        }

                        break;

                    case BatchFinalizedEvent batchFinalized:
                        if (batchFinalized.Id == intent.BatchId)
                        {
                            await HandleBatchFinalizedAsync(intent, batchFinalized, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            TriggerStreamUpdate();
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
    /// Handles a batch failure by resetting the intent for re-registration.
    ///
    /// <para><b>Batch Failure Recovery Flow:</b></para>
    /// <list type="number">
    ///   <item>Intent state is reset to <see cref="ArkIntentState.WaitingToSubmit"/> with IntentId cleared</item>
    ///   <item><see cref="IntentSynchronizationService"/> picks up the intent via GetUnsubmittedIntents()</item>
    ///   <item>Re-registration is attempted with arkd:
    ///     <list type="bullet">
    ///       <item>If arkd requeued the intent: AlreadyLockedVtxoException is thrown, triggering delete + re-register</item>
    ///       <item>If we got a conviction (our fault): re-registration succeeds with a new IntentId</item>
    ///       <item>If we're banned (3 convictions): re-registration fails, intent is cancelled</item>
    ///     </list>
    ///   </item>
    ///   <item>On successful re-registration, intent moves to WaitingForBatch and is picked up by BatchManagementService again</item>
    /// </list>
    ///
    /// <para><b>Note:</b> arkd tracks "convictions" - if a batch fails due to your intent not signing,
    /// you get a conviction and the intent is NOT automatically requeued. After 3 convictions,
    /// the VTXOs used in that intent are temporarily banned from spending/refreshing.</para>
    /// </summary>
    private async Task HandleBatchFailedAsync(
        ArkIntent intent,
        BatchFailedEvent batchEvent,
        CancellationToken cancellationToken)
    {
        await SaveToStorage(intent.IntentId!, arkIntent =>
            (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
            {
                State = ArkIntentState.WaitingToSubmit,
                BatchId = null,
                IntentId = null,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

        await eventHandlers.SafeHandleEventAsync(
            new PostBatchSessionEvent(intent, null, ActionState.Failed, batchEvent.Reason),
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
        IWalletProvider walletProvider,
        ICoinService coinService,
        ISafetyService safetyService)
        : this(intentStorage, clientTransport, vtxoStorage, walletProvider, coinService, safetyService, [])
    {
    }

    public BatchManagementService(IIntentStorage intentStorage,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IWalletProvider walletProvider,
        ICoinService coinService,
        ISafetyService safetyService,
        ILogger<BatchManagementService> logger)
        : this(intentStorage, clientTransport, vtxoStorage, walletProvider, coinService, safetyService, [], logger)
    {
    }
}
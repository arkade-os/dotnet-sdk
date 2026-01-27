using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Transport;
using NArk.Core.Extensions;

namespace NArk.Core.Services;

public class IntentSynchronizationService(
    IIntentStorage intentStorage,
    IClientTransport clientTransport,
    ISafetyService safetyService,
    IEnumerable<IEventHandler<PostIntentSubmissionEvent>> eventHandlers,
    ILogger<IntentSynchronizationService>? logger = null) : IAsyncDisposable
{

    public IntentSynchronizationService(IIntentStorage intentStorage,
        IClientTransport clientTransport,
        ISafetyService safetyService) : this(intentStorage, clientTransport, safetyService, [], null)
    {

    }

    public IntentSynchronizationService(IIntentStorage intentStorage,
        IClientTransport clientTransport,
        ISafetyService safetyService,
        ILogger<IntentSynchronizationService> logger) : this(intentStorage, clientTransport, safetyService, [], logger)
    {

    }

    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly Channel<string> _submitTriggerChannel = Channel.CreateUnbounded<string>();
    private Task? _intentSubmitLoop;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        logger?.LogInformation("Starting intent synchronization service");
        intentStorage.IntentChanged += OnIntentChanged;
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _intentSubmitLoop = DoIntentSubmitLoop(multiToken.Token);
        _submitTriggerChannel.Writer.TryWrite("START");
        return Task.CompletedTask;
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        _submitTriggerChannel.Writer.TryWrite("INTENT_CHANGED");
    }

    private async Task DoIntentSubmitLoop(CancellationToken token)
    {
        try
        {
            await foreach (var _ in _submitTriggerChannel.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();

                var intentsToSubmit = await intentStorage.GetUnsubmittedIntents(DateTimeOffset.UtcNow, token);
                foreach (var intentToSubmit in intentsToSubmit)
                {
                    // In case storage did not respect our wish...
                    if (intentToSubmit.ValidFrom > DateTimeOffset.UtcNow ||
                            intentToSubmit.ValidUntil < DateTimeOffset.UtcNow)
                        continue;

                    await SubmitIntent(intentToSubmit, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("Intent submit loop cancelled");
        }
    }

    private async Task SubmitIntent(ArkIntent intentToSubmit, CancellationToken token)
    {
        logger?.LogDebug("Submitting intent {IntentTxId}", intentToSubmit.IntentTxId);
        await using var @lock = await safetyService.LockKeyAsync($"intent::{intentToSubmit.IntentTxId}", token);
        var intentAfterLock = await intentStorage.GetIntentByIntentTxId(intentToSubmit.IntentTxId, token);
        if (intentAfterLock is null)
        {
            logger?.LogError("Intent {IntentTxId} disappeared from storage mid-action", intentToSubmit.IntentTxId);
            throw new Exception("Should not happen, intent disappeared from storage mid-action");
        }

        try
        {
            try
            {
                var intentId =
                    await clientTransport.RegisterIntent(intentAfterLock, token);

                var now = DateTimeOffset.UtcNow;

                await intentStorage.SaveIntent(
                    intentAfterLock.WalletId,
                    intentAfterLock with
                    {
                        IntentId = intentId,
                        State = ArkIntentState.WaitingForBatch,
                        UpdatedAt = now
                    }, token);

                logger?.LogInformation("Intent {IntentTxId} registered successfully with server intent id {ServerIntentId}", intentToSubmit.IntentTxId, intentId);
                await eventHandlers.SafeHandleEventAsync(new PostIntentSubmissionEvent(intentAfterLock, now, true,
                    ActionState.Successful, null), token);
            }
            catch (AlreadyLockedVtxoException ex)
            {
                logger?.LogWarning(0, ex, "Intent {IntentTxId} vtxo already locked, deleting and re-registering", intentToSubmit.IntentTxId);
                await clientTransport.DeleteIntent(intentAfterLock, token);

                var intentId =
                    await clientTransport.RegisterIntent(intentAfterLock, token);

                var now = DateTimeOffset.UtcNow;

                await intentStorage.SaveIntent(
                    intentAfterLock.WalletId,
                    intentAfterLock with
                    {
                        IntentId = intentId,
                        State = ArkIntentState.WaitingForBatch,
                        UpdatedAt = now
                    }, token);

                logger?.LogInformation("Intent {IntentTxId} re-registered successfully after deletion", intentToSubmit.IntentTxId);
                await eventHandlers.SafeHandleEventAsync(new PostIntentSubmissionEvent(intentAfterLock, now, false,
                    ActionState.Successful, null), token);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Intent {IntentTxId} submission failed", intentToSubmit.IntentTxId);
            var now = DateTimeOffset.UtcNow;

            await intentStorage.SaveIntent(
                intentAfterLock.WalletId,
                intentAfterLock with
                {
                    State = ArkIntentState.Cancelled,
                    UpdatedAt = now
                }, token);

            await eventHandlers.SafeHandleEventAsync(new PostIntentSubmissionEvent(intentAfterLock, now, false,
                ActionState.Failed, $"Intent submission failed with ex: {ex}"), token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        logger?.LogDebug("Disposing intent synchronization service");
        intentStorage.IntentChanged -= OnIntentChanged;

        await _shutdownCts.CancelAsync();

        try
        {
            if (_intentSubmitLoop is not null)
                await _intentSubmitLoop;
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("Intent submit loop cancelled during disposal");
        }

        logger?.LogInformation("Intent synchronization service disposed");
    }


}
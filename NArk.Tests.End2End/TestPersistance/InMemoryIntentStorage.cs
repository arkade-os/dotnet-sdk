using NArk.Abstractions.Intents;
using NBitcoin;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryIntentStorage : IIntentStorage
{
    public event EventHandler<ArkIntent>? IntentChanged;
    private readonly Dictionary<string, HashSet<ArkIntent>> _intents = new();

    public Task SaveIntent(string walletIdentifier, ArkIntent intent, CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            if (_intents.TryGetValue(walletIdentifier, out var intents))
            {
                intents.Remove(intent);
                intents.Add(intent);
            }
            else
                _intents[walletIdentifier] = new HashSet<ArkIntent>(ArkIntent.IntentTxIdComparer) { intent };
        }

        try
        {
            IntentChanged?.Invoke(this, intent);
        }
        catch
        {
            // ignored
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<ArkIntent>> GetIntents(string walletId, CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            return Task.FromResult<IReadOnlyCollection<ArkIntent>>(_intents[walletId]);
        }
    }

    public Task<ArkIntent?> GetIntentByIntentTxId(string intentTxId, CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            return Task.FromResult(
                _intents
                    .FirstOrDefault(i => i.Value.Any(intent => intent.IntentTxId == intentTxId))
                    .Value
                    ?.FirstOrDefault(intent => intent.IntentTxId == intentTxId));

        }
    }

    public Task<ArkIntent?> GetIntentByIntentId(string walletId, string intentId, CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            return Task.FromResult(_intents[walletId].FirstOrDefault(intent => intent.IntentId == intentId));
        }
    }

    public Task<IReadOnlyCollection<ArkIntent>> GetIntentsByInputs(string walletId, OutPoint[] inputs,
        bool pendingOnly = true, CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            return Task.FromResult<IReadOnlyCollection<ArkIntent>>(!_intents.TryGetValue(walletId, out var intents)
                ? []
                : intents.Where(intent => inputs.Intersect(intent.IntentVtxos).Any()).ToList());
        }
    }

    public Task<IReadOnlyCollection<ArkIntent>> GetUnsubmittedIntents(DateTimeOffset? validAt = null, CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            var allIntents =
                _intents
                    .SelectMany(intents =>
                        intents
                            .Value
                            .Where(intent => intent is { State: ArkIntentState.WaitingToSubmit, IntentId: null })
                    );

            if (validAt is not { } validAtValue)
                return Task.FromResult<IReadOnlyCollection<ArkIntent>>(allIntents.ToArray());

            return Task.FromResult<IReadOnlyCollection<ArkIntent>>(
                allIntents.Where(i => i.ValidFrom < validAtValue && i.ValidUntil > validAtValue).ToArray()
            );

        }
    }

    public Task<IReadOnlyCollection<ArkIntent>> GetActiveIntents(CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            return Task.FromResult<IReadOnlyCollection<ArkIntent>>(_intents.SelectMany(i =>
                i.Value.Where(intent => intent is { State: ArkIntentState.WaitingForBatch, IntentId: not null })).ToArray());
        }
    }
}
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

    public Task<IReadOnlyCollection<ArkIntent>> GetIntents(
        string[]? walletIds = null,
        string[]? intentTxIds = null,
        string[]? intentIds = null,
        OutPoint[]? containingInputs = null,
        ArkIntentState[]? states = null,
        DateTimeOffset? validAt = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        lock (_intents)
        {
            IEnumerable<ArkIntent> query = _intents.Values.SelectMany(x => x);

            // Filter by wallet IDs
            if (walletIds is { Length: > 0 })
            {
                var walletSet = walletIds.ToHashSet();
                query = query.Where(i => walletSet.Contains(i.WalletId));
            }

            // Filter by intent transaction IDs
            if (intentTxIds is { Length: > 0 })
            {
                var txIdSet = intentTxIds.ToHashSet();
                query = query.Where(i => txIdSet.Contains(i.IntentTxId));
            }

            // Filter by intent IDs
            if (intentIds is { Length: > 0 })
            {
                var idSet = intentIds.ToHashSet();
                query = query.Where(i => i.IntentId != null && idSet.Contains(i.IntentId));
            }

            // Filter by containing inputs
            if (containingInputs is { Length: > 0 })
            {
                query = query.Where(i => containingInputs.Intersect(i.IntentVtxos).Any());
            }

            // Filter by states
            if (states is { Length: > 0 })
            {
                query = query.Where(i => states.Contains(i.State));
            }

            // Filter by validity time
            if (validAt.HasValue)
            {
                query = query.Where(i =>
                    i.ValidFrom <= validAt.Value && i.ValidUntil >= validAt.Value);
            }

            // Pagination
            if (skip.HasValue)
            {
                query = query.Skip(skip.Value);
            }

            if (take.HasValue)
            {
                query = query.Take(take.Value);
            }

            return Task.FromResult<IReadOnlyCollection<ArkIntent>>(query.ToList());
        }
    }
}
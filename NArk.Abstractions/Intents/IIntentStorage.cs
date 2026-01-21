using NBitcoin;

namespace NArk.Abstractions.Intents;

public interface IIntentStorage
{
    public event EventHandler<ArkIntent>? IntentChanged;

    public Task SaveIntent(string walletId, ArkIntent intent, CancellationToken cancellationToken = default);
    public Task<IReadOnlyCollection<ArkIntent>> GetIntents(string walletId, CancellationToken cancellationToken = default);
    public Task<ArkIntent?> GetIntentByInternalId(Guid internalId, CancellationToken cancellationToken = default);
    public Task<ArkIntent?> GetIntentByIntentId(string walletId, string intentId, CancellationToken cancellationToken = default);
    public Task<IReadOnlyCollection<ArkIntent>> GetIntentsByInputs(string walletId,
        OutPoint[] inputs, bool pendingOnly = true, CancellationToken cancellationToken = default);
    public Task<IReadOnlyCollection<ArkIntent>> GetUnsubmittedIntents(DateTimeOffset? validAt = null, CancellationToken cancellationToken = default);
    public Task<IReadOnlyCollection<ArkIntent>> GetActiveIntents(CancellationToken cancellationToken = default);
}
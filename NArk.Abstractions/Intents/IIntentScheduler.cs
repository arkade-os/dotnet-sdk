namespace NArk.Abstractions.Intents;

public interface IIntentScheduler
{
    Task<IReadOnlyCollection<ArkIntentSpec>> GetIntentsToSubmit(IReadOnlyCollection<ArkCoin> unspentVtxos,
        CancellationToken cancellationToken = default);
}
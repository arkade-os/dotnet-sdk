namespace NArk.Abstractions.Intents;

/// <summary>
/// Decides which intents to submit on the next batch cycle.
/// Implement this to drive automatic VTXO rollover (e.g. refresh before expiry).
/// </summary>
public interface IIntentScheduler
{
    /// <summary>
    /// Given the current unspent coins, returns the set of intents to submit.
    /// Called periodically by the SDK's intent generation loop (default every 5 minutes).
    /// </summary>
    Task<IReadOnlyCollection<ArkIntentSpec>> GetIntentsToSubmit(IReadOnlyCollection<ArkCoin> unspentVtxos,
        CancellationToken cancellationToken = default);
}

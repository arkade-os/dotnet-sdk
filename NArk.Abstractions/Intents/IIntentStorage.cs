using NBitcoin;

namespace NArk.Abstractions.Intents;

public interface IIntentStorage
{
    public event EventHandler<ArkIntent>? IntentChanged;

    public Task SaveIntent(string walletId, ArkIntent intent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query intents with explicit filter parameters.
    /// Adding new parameters will cause compile errors for implementors, ensuring they handle new filters.
    /// </summary>
    /// <param name="walletIds">Filter by wallet IDs. If null/empty, all wallets.</param>
    /// <param name="intentTxIds">Filter by intent transaction IDs. If null/empty, no filter.</param>
    /// <param name="intentIds">Filter by intent IDs. If null/empty, no filter.</param>
    /// <param name="containingInputs">Filter to intents containing any of these input outpoints. If null/empty, no filter.</param>
    /// <param name="states">Filter by intent states. If null/empty, all states.</param>
    /// <param name="validAt">Filter to intents valid at this time. If null, no time filter.</param>
    /// <param name="skip">Number of records to skip (for pagination). If null, no skip.</param>
    /// <param name="take">Number of records to take (for pagination). If null, no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyCollection<ArkIntent>> GetIntents(
        string[]? walletIds = null,
        string[]? intentTxIds = null,
        string[]? intentIds = null,
        OutPoint[]? containingInputs = null,
        ArkIntentState[]? states = null,
        DateTimeOffset? validAt = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets outpoints of VTXOs that are locked by pending intents.
    /// Used for balance calculations to exclude locked VTXOs from available balance.
    /// </summary>
    /// <param name="walletId">The wallet ID to get locked outpoints for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of outpoints locked by pending intents</returns>
    Task<IReadOnlyCollection<OutPoint>> GetLockedVtxoOutpoints(
        string walletId,
        CancellationToken cancellationToken = default);
}
using NArk.Abstractions.Scripts;
using NArk.ArkadeIntents.Models;

namespace NArk.ArkadeIntents;

/// <summary>
/// Persistence and change-notification for non-interactive swap intents. Also the single
/// <see cref="IActiveScriptsProvider"/> that feeds the shared <c>VtxoSynchronizationService</c> the
/// covenant scripts of pending swaps — so their VTXOs are watched (and the watch survives a restart,
/// unlike in-memory tracking).
/// </summary>
public interface IArkadeIntentStorage : IActiveScriptsProvider
{
    /// <summary>Raised whenever a swap intent is saved or its status changes.</summary>
    event EventHandler<ArkadeSwapIntent>? SwapsChanged;

    /// <summary>Query swap intents by status, covenant script and/or wallet.</summary>
    Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
        ArkadeSwapIntentStatus? status = null,
        string? swapPkScript = null,
        string[]? walletIds = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    /// <summary>Insert or update a swap intent, keyed by <see cref="ArkadeSwapIntent.Id"/>.</summary>
    Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transition the <b>in-flight</b> swap on the given covenant script to <paramref name="status"/>
    /// (recording <paramref name="spentTxid"/> when spent). Only swaps that are still
    /// <see cref="ArkadeSwapIntentStatus.Pending"/> or <see cref="ArkadeSwapIntentStatus.Refundable"/>
    /// are touched — so a swap already moved to <see cref="ArkadeSwapIntentStatus.Cancelling"/> is never
    /// read as a fill (the race guard). Returns <c>false</c> when no in-flight swap matches.
    /// </summary>
    Task<bool> UpdateStatus(
        string swapPkScript,
        ArkadeSwapIntentStatus status,
        string? spentTxid = null,
        CancellationToken cancellationToken = default);

    /// <summary>The covenant scripts of swaps still in flight — the set the sync service watches.</summary>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The scripts to watch.</returns>
    /// <remarks>
    /// Refundable swaps stay watched: their deposit is still sitting in the covenant, and the refund
    /// that ends them is observable on the very same script.
    /// </remarks>
    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        var pending = await GetArkadeSwapIntents(
            status: ArkadeSwapIntentStatus.Pending, cancellationToken: cancellationToken);
        var refundable = await GetArkadeSwapIntents(
            status: ArkadeSwapIntentStatus.Refundable, cancellationToken: cancellationToken);

        return pending.Concat(refundable).Select(s => s.SwapPkScript).ToHashSet();
    }
}

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
    /// Transition the <b>pending</b> swap on the given covenant script to <paramref name="status"/>
    /// (recording <paramref name="spentTxid"/> when fulfilled). Only pending swaps are touched — so a
    /// swap already moved to <see cref="ArkadeSwapIntentStatus.Cancelling"/> is never read as a fill (the
    /// race guard). Returns <c>false</c> when no pending swap matches.
    /// </summary>
    Task<bool> UpdateStatus(
        string swapPkScript,
        ArkadeSwapIntentStatus status,
        string? spentTxid = null,
        CancellationToken cancellationToken = default);

    /// <summary>The covenant scripts of pending swaps — the set the sync service watches.</summary>
    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
        => (await GetArkadeSwapIntents(status: ArkadeSwapIntentStatus.Pending, cancellationToken: cancellationToken))
            .Select(s => s.SwapPkScript)
            .ToHashSet();
}

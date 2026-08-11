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

    /// <summary>Query swap intents by id, status, covenant script and/or wallet.</summary>
    /// <param name="id">A single intent's id.</param>
    /// <param name="status">Only intents in this status.</param>
    /// <param name="swapPkScript">Only the intent on this covenant script.</param>
    /// <param name="walletIds">Only intents belonging to these wallets.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching intents.</returns>
    /// <remarks>
    /// Implementations must apply every filter given. A store that ignores one does not merely return
    /// too much: callers use these to identify a single swap before moving its money, so a filter
    /// quietly dropped is a caller acting on somebody else's intent.
    /// </remarks>
    Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
        string? id = null,
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
    /// that ends them is observable on the very same script. Claimable ones stay watched for the
    /// sharper version of the same reason — on the receive corridor that state means the money is
    /// ours to take, on a clock that runs out in hours, and dropping the script there is dropping it
    /// exactly when the spend that matters is about to happen.
    ///
    /// Funding is deliberately left out. Its lockup may not exist yet, so polling would watch a
    /// script that may never be funded; reconciliation reads that state on startup, which is when a
    /// swap recorded before its own spend actually needs re-examining.
    /// </remarks>
    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        var pending = await GetArkadeSwapIntents(
            status: ArkadeSwapIntentStatus.Pending, cancellationToken: cancellationToken);
        var refundable = await GetArkadeSwapIntents(
            status: ArkadeSwapIntentStatus.Refundable, cancellationToken: cancellationToken);
        var claimable = await GetArkadeSwapIntents(
            status: ArkadeSwapIntentStatus.Claimable, cancellationToken: cancellationToken);

        return pending.Concat(refundable).Concat(claimable).Select(s => s.SwapPkScript).ToHashSet();
    }
}

/// <summary>Lookups over <see cref="IArkadeIntentStorage"/> that every store gets for free.</summary>
public static class ArkadeIntentStorageExtensions
{
    /// <summary>Find one intent by id, or <c>null</c>.</summary>
    /// <param name="storage">The store to ask.</param>
    /// <param name="id">The intent's id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The intent, or <c>null</c> when no such swap exists.</returns>
    /// <remarks>
    /// An extension rather than a member of the interface: it is a shorthand for one filtered query,
    /// so there is nothing here an implementation could usefully do differently, and keeping it out
    /// of the contract means no store — real or test double — can answer it inconsistently with the
    /// filter it delegates to.
    /// </remarks>
    public static async Task<ArkadeSwapIntent?> GetArkadeSwapIntent(
        this IArkadeIntentStorage storage, string id, CancellationToken cancellationToken = default) =>
        (await storage.GetArkadeSwapIntents(id: id, cancellationToken: cancellationToken)).SingleOrDefault();
}

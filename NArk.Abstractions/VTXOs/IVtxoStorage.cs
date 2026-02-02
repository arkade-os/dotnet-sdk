using NArk.Abstractions.Scripts;
using NBitcoin;

namespace NArk.Abstractions.VTXOs;

public interface IVtxoStorage : IActiveScriptsProvider
{
    public event EventHandler<ArkVtxo>? VtxosChanged;

    Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query VTXOs with explicit filter parameters.
    /// Adding new parameters will cause compile errors for implementors, ensuring they handle new filters.
    /// </summary>
    /// <param name="scripts">Filter by script hex strings. If null, no script filter applied.</param>
    /// <param name="outpoints">Filter by specific outpoints. If null, no outpoint filter applied.</param>
    /// <param name="walletIds">Filter by wallet IDs (requires join with contracts). If null, no wallet filter.</param>
    /// <param name="includeSpent">Include spent VTXOs. Default: false (unspent only).</param>
    /// <param name="searchText">Search text for TransactionId or Script. If null, no text search.</param>
    /// <param name="skip">Number of records to skip (for pagination). If null, no skip.</param>
    /// <param name="take">Number of records to take (for pagination). If null, no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(
        IReadOnlyCollection<string>? scripts = null,
        IReadOnlyCollection<OutPoint>? outpoints = null,
        string[]? walletIds = null,
        bool includeSpent = false,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        return (await GetVtxos(cancellationToken: cancellationToken)).Select(vtxo => vtxo.Script).ToHashSet();
    }
}
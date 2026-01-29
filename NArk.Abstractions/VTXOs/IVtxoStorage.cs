using NArk.Abstractions.Scripts;

namespace NArk.Abstractions.VTXOs;

public interface IVtxoStorage : IActiveScriptsProvider
{
    public event EventHandler<ArkVtxo>? VtxosChanged;

    Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unified VTXO query method with flexible filtering.
    /// </summary>
    Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default);

    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        return (await GetVtxos(VtxoFilter.Unspent, cancellationToken)).Select(vtxo => vtxo.Script).ToHashSet();
    }
}
using NArk.Abstractions.Scripts;
using NBitcoin;

namespace NArk.Abstractions.VTXOs;

public interface IVtxoStorage : IActiveScriptsProvider
{
    public event EventHandler<ArkVtxo>? VtxosChanged;

    Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default);
    Task<ArkVtxo?> GetVtxoByOutPoint(OutPoint outpoint, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ArkVtxo>> GetVtxosByScripts(IReadOnlyCollection<string> scripts, bool allowSpent = false
        , CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(CancellationToken cancellationToken = default);

    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        return (await GetUnspentVtxos(cancellationToken)).Select(vtxo => vtxo.Script).ToHashSet();
    }
}
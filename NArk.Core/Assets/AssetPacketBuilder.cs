using NBitcoin;

namespace NArk.Core.Assets;

/// <summary>
/// Builds an asset packet OP_RETURN TxOut from pre-mapped asset input/output tuples.
/// Callers are responsible for mapping their own vin/vout indices (e.g. applying BIP322 +1 offset).
/// </summary>
public static class AssetPacketBuilder
{
    /// <summary>
    /// Builds an asset packet OP_RETURN TxOut.
    /// </summary>
    /// <param name="inputs">Asset inputs: (assetId, vin, amount) — caller maps vin indices.</param>
    /// <param name="outputs">Explicit asset outputs: (assetId, vout, amount). Null means no explicit outputs.</param>
    /// <param name="changeVout">Output index where unaccounted asset change is assigned.</param>
    /// <returns>OP_RETURN TxOut, or null if no assets present.</returns>
    public static TxOut? Build(
        IReadOnlyCollection<(string assetId, ushort vin, ulong amount)> inputs,
        IReadOnlyCollection<(string assetId, ushort vout, ulong amount)>? outputs,
        ushort changeVout)
    {
        var inputsByAsset = new Dictionary<string, List<(ushort vin, ulong amount)>>();
        foreach (var (assetId, vin, amount) in inputs)
        {
            if (!inputsByAsset.TryGetValue(assetId, out var list))
                inputsByAsset[assetId] = list = [];
            list.Add((vin, amount));
        }

        if (inputsByAsset.Count == 0)
            return null;

        var outputsByAsset = new Dictionary<string, List<(ushort vout, ulong amount)>>();
        if (outputs is not null)
        {
            foreach (var (assetId, vout, amount) in outputs)
            {
                if (!outputsByAsset.TryGetValue(assetId, out var list))
                    outputsByAsset[assetId] = list = [];
                list.Add((vout, amount));
            }
        }

        var allAssetIds = new HashSet<string>(inputsByAsset.Keys);
        allAssetIds.UnionWith(outputsByAsset.Keys);

        var groups = new List<AssetGroup>();
        foreach (var assetIdStr in allAssetIds)
        {
            var assetId = AssetId.FromString(assetIdStr);
            var groupInputs = inputsByAsset.GetValueOrDefault(assetIdStr)?
                .Select(x => AssetInput.Create(x.vin, x.amount))
                .ToList() ?? [];

            var groupOutputs = outputsByAsset.GetValueOrDefault(assetIdStr)?
                .Select(x => AssetOutput.Create(x.vout, x.amount))
                .ToList() ?? [];

            var totalIn = inputsByAsset.GetValueOrDefault(assetIdStr)?
                .Aggregate(0UL, (sum, x) => sum + x.amount) ?? 0;
            var totalExplicitOut = groupOutputs.Aggregate(0UL, (sum, o) => sum + o.Amount);

            if (totalIn > totalExplicitOut)
            {
                var remaining = totalIn - totalExplicitOut;
                var existingIdx = groupOutputs.FindIndex(o => o.Vout == changeVout);
                if (existingIdx >= 0)
                {
                    var existing = groupOutputs[existingIdx];
                    groupOutputs[existingIdx] = AssetOutput.Create(changeVout, existing.Amount + remaining);
                }
                else
                {
                    groupOutputs.Add(AssetOutput.Create(changeVout, remaining));
                }
            }

            groups.Add(AssetGroup.Create(assetId, null, groupInputs, groupOutputs, []));
        }

        return Packet.Create(groups).ToTxOut();
    }
}

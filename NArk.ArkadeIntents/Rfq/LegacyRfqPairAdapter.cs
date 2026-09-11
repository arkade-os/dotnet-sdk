using NArk.ArkadeIntents.SolverRegistry;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>Explicitly adapts canonical discovery identities to the current solver's legacy RFQ vocabulary.</summary>
public static class LegacyRfqPairAdapter
{
    /// <summary>Builds a current BTC/Arkade-asset RFQ pair; ambiguous external ticker mappings are refused.</summary>
    public static string FromCanonical(AssetIdentifier from, AssetIdentifier to)
    {
        if (from.ChainReference != to.ChainReference)
            throw new ArgumentException("Legacy RFQ pairs cannot preserve distinct chain references.");
        return $"{Leg(from)}->{Leg(to)}";
    }

    internal static string Leg(AssetIdentifier asset)
    {
        var corridor = asset.Namespace switch
        {
            "arkade" => "arkade", "bolt11" => "lightning", "bitcoin" => "onchain",
            _ => throw new NotSupportedException("An external asset requires an explicit solver-specific RFQ profile."),
        };
        var id = asset.Asset.StartsWith("slip44:") ? "BTC"
            : asset.Namespace == "arkade" ? asset.Asset["asset:".Length..]
            : throw new NotSupportedException("This legacy RFQ profile supports issued assets only on Arkade.");
        return $"{corridor}:{id}";
    }
}

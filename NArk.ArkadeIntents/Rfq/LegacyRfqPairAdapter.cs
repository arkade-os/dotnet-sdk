using NArk.ArkadeIntents.SolverRegistry;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>Explicitly adapts canonical discovery identities to the current solver's legacy RFQ vocabulary.</summary>
public static class LegacyRfqPairAdapter
{
    /// <summary>Builds a current BTC/Arkade-asset RFQ pair; ambiguous external ticker mappings are refused.</summary>
    public static string FromCanonical(AssetIdentifier from, AssetIdentifier to)
    {
        if (from.Namespace == "arkade" && (from.Asset is "slip44:0" or "slip44:1")
            && to.Namespace == "eip155" && to.Asset.StartsWith("erc20:", StringComparison.Ordinal))
        {
            var token = EvmWire.RequireNonZeroAddress(
                to.Asset["erc20:".Length..], nameof(to), lowerCase: true);
            return $"arkade:BTC->ethereum:{token}";
        }
        if (from.Namespace == "arkade" && to.Namespace == "eip155")
            throw new ArgumentException("EVM RFQs require Arkade BTC and an explicit canonical ERC20 address.");
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

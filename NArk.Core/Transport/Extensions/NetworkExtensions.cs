using NBitcoin;

namespace NArk.Core.Transport.Extensions;

/// <summary>
/// Resolves arkd network names to NBitcoin <see cref="Network"/> instances.
/// arkd reports raw network strings (e.g., "mutinynet", "bitcoin", "signet")
/// that may not match NBitcoin's registered network names.
/// </summary>
internal static class NetworkExtensions
{
    /// <summary>
    /// Maps an arkd network name to an NBitcoin <see cref="Network"/>.
    /// Handles standard names via <see cref="Network.GetNetwork"/> and falls back
    /// to known aliases for custom signets and other variants.
    /// </summary>
    public static Network ResolveArkNetwork(string networkName)
    {
        // NBitcoin knows: Main/MainNet, TestNet/TestNet3, RegTest, Signet (in newer versions)
        if (Network.GetNetwork(networkName) is { } net)
            return net;

        return networkName.ToLowerInvariant() switch
        {
            "bitcoin" => Network.Main,
            "mutinynet" => Network.TestNet,
            "liquid" => Network.Main,
            "liquidtestnet" or "liquid-testnet" => Network.TestNet,
            _ => throw new InvalidOperationException(
                $"Ark server advertises unknown network '{networkName}'. " +
                "If this is a custom signet, please report this so it can be added.")
        };
    }
}

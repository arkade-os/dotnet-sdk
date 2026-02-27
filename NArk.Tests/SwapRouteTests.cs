using NArk.Swaps.Abstractions;

namespace NArk.Tests;

[TestFixture]
public class SwapRouteTests
{
    [Test]
    public void WellKnownAssets_HaveCorrectNetworkAndId()
    {
        Assert.That(SwapAsset.BtcOnchain.Network, Is.EqualTo(SwapNetwork.BitcoinOnchain));
        Assert.That(SwapAsset.BtcOnchain.AssetId, Is.EqualTo("BTC"));
        Assert.That(SwapAsset.BtcLightning.Network, Is.EqualTo(SwapNetwork.Lightning));
        Assert.That(SwapAsset.ArkBtc.Network, Is.EqualTo(SwapNetwork.Ark));
    }

    [Test]
    public void Erc20Factory_CreatesAssetWithContractAddress()
    {
        var usdc = SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359");
        Assert.That(usdc.Network, Is.EqualTo(SwapNetwork.EvmPolygon));
        Assert.That(usdc.AssetId, Is.EqualTo("0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359"));
    }

    [Test]
    public void SwapRoute_EqualityByValue()
    {
        var route1 = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);
        var route2 = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);
        Assert.That(route1, Is.EqualTo(route2));
    }

    [Test]
    public void SwapRoute_DifferentDirections_AreNotEqual()
    {
        var forward = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);
        var reverse = new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc);
        Assert.That(forward, Is.Not.EqualTo(reverse));
    }

    [Test]
    public void SwapRoute_WorksInHashSet()
    {
        var set = new HashSet<SwapRoute>
        {
            new(SwapAsset.ArkBtc, SwapAsset.BtcOnchain),
            new(SwapAsset.ArkBtc, SwapAsset.BtcOnchain), // duplicate
            new(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),
        };
        Assert.That(set, Has.Count.EqualTo(2));
    }
}

using NArk.Abstractions.VTXOs;
using NArk.Swaps.Services;

namespace NArk.Tests;

[TestFixture]
public class MergeAssetsTests
{
    [Test]
    public void BothNull_ReturnsNull()
    {
        var result = PaymentTrackingService.MergeAssets(null, null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ExistingNull_ReturnsIncoming()
    {
        var incoming = new List<VtxoAsset> { new("asset1", 100) };
        var result = PaymentTrackingService.MergeAssets(null, incoming);
        Assert.That(result, Is.SameAs(incoming));
    }

    [Test]
    public void IncomingNull_ReturnsExisting()
    {
        var existing = new List<VtxoAsset> { new("asset1", 100) };
        var result = PaymentTrackingService.MergeAssets(existing, null);
        Assert.That(result, Is.SameAs(existing));
    }

    [Test]
    public void IncomingEmpty_ReturnsExisting()
    {
        var existing = new List<VtxoAsset> { new("asset1", 100) };
        var result = PaymentTrackingService.MergeAssets(existing, new List<VtxoAsset>());
        Assert.That(result, Is.SameAs(existing));
    }

    [Test]
    public void DisjointAssets_Concatenates()
    {
        var existing = new List<VtxoAsset> { new("asset1", 100) };
        var incoming = new List<VtxoAsset> { new("asset2", 200) };

        var result = PaymentTrackingService.MergeAssets(existing, incoming)!;

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Single(a => a.AssetId == "asset1").Amount, Is.EqualTo(100UL));
        Assert.That(result.Single(a => a.AssetId == "asset2").Amount, Is.EqualTo(200UL));
    }

    [Test]
    public void SameAsset_SumsAmounts()
    {
        var existing = new List<VtxoAsset> { new("asset1", 100) };
        var incoming = new List<VtxoAsset> { new("asset1", 250) };

        var result = PaymentTrackingService.MergeAssets(existing, incoming)!;

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].AssetId, Is.EqualTo("asset1"));
        Assert.That(result[0].Amount, Is.EqualTo(350UL));
    }

    [Test]
    public void DuplicateAssetIdInExisting_HandlesGracefully()
    {
        // Corrupted data: same AssetId appears twice in existing
        var existing = new List<VtxoAsset> { new("asset1", 100), new("asset1", 50) };
        var incoming = new List<VtxoAsset> { new("asset1", 200) };

        var result = PaymentTrackingService.MergeAssets(existing, incoming)!;

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Amount, Is.EqualTo(350UL)); // 100 + 50 + 200
    }

    [Test]
    public void MultipleAssets_MixedMerge()
    {
        var existing = new List<VtxoAsset> { new("a", 10), new("b", 20) };
        var incoming = new List<VtxoAsset> { new("b", 30), new("c", 40) };

        var result = PaymentTrackingService.MergeAssets(existing, incoming)!;

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Single(x => x.AssetId == "a").Amount, Is.EqualTo(10UL));
        Assert.That(result.Single(x => x.AssetId == "b").Amount, Is.EqualTo(50UL));
        Assert.That(result.Single(x => x.AssetId == "c").Amount, Is.EqualTo(40UL));
    }
}

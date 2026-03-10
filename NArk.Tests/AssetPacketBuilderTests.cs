using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Tests;

[TestFixture]
public class AssetPacketBuilderTests
{
    // 34 bytes = 32B genesis txid + 2B group index LE, hex-encoded (68 chars)
    private const string FakeAssetId =
        "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b20000";

    private const string FakeAssetId2 =
        "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c30000";

    [Test]
    public void Build_ReturnsNull_WhenNoAssetInputs()
    {
        var result = AssetPacketBuilder.Build([], null, changeVout: 0);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Build_AllToChange_WhenNoExplicitOutputs()
    {
        var inputs = new[] { (FakeAssetId, (ushort)1, 1000UL) };
        var result = AssetPacketBuilder.Build(inputs, null, changeVout: 0);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ScriptPubKey.IsUnspendable, Is.True, "Asset packet should be OP_RETURN");

        var packet = Packet.FromScript(result.ScriptPubKey);
        Assert.That(packet.Groups, Has.Count.EqualTo(1));
        Assert.That(packet.Groups[0].Outputs, Has.Count.EqualTo(1));
        Assert.That(packet.Groups[0].Outputs[0].Vout, Is.EqualTo(0));
        Assert.That(packet.Groups[0].Outputs[0].Amount, Is.EqualTo(1000UL));
    }

    [Test]
    public void Build_SplitsToExplicitAndChange()
    {
        var inputs = new[] { (FakeAssetId, (ushort)0, 1000UL) };
        var outputs = new[] { (FakeAssetId, (ushort)1, 400UL) };
        var result = AssetPacketBuilder.Build(inputs, outputs, changeVout: 0);

        Assert.That(result, Is.Not.Null);
        var packet = Packet.FromScript(result!.ScriptPubKey);
        Assert.That(packet.Groups, Has.Count.EqualTo(1));
        Assert.That(packet.Groups[0].Inputs, Has.Count.EqualTo(1));
        Assert.That(packet.Groups[0].Outputs, Has.Count.EqualTo(2));
    }

    [Test]
    public void Build_NoChange_WhenFullyAllocated()
    {
        var inputs = new[] { (FakeAssetId, (ushort)0, 1000UL) };
        var outputs = new[] { (FakeAssetId, (ushort)0, 1000UL) };
        var result = AssetPacketBuilder.Build(inputs, outputs, changeVout: 0);

        Assert.That(result, Is.Not.Null);
        var packet = Packet.FromScript(result!.ScriptPubKey);
        Assert.That(packet.Groups[0].Outputs, Has.Count.EqualTo(1));
        Assert.That(packet.Groups[0].Outputs[0].Amount, Is.EqualTo(1000UL));
    }

    [Test]
    public void Build_MergesChangeIntoExistingVout()
    {
        var inputs = new[] { (FakeAssetId, (ushort)0, 1000UL) };
        // Explicit output at same vout as changeVout — should merge
        var outputs = new[] { (FakeAssetId, (ushort)0, 400UL) };
        var result = AssetPacketBuilder.Build(inputs, outputs, changeVout: 0);

        Assert.That(result, Is.Not.Null);
        var packet = Packet.FromScript(result!.ScriptPubKey);
        // 400 explicit + 600 change = 1000 at vout 0
        Assert.That(packet.Groups[0].Outputs, Has.Count.EqualTo(1));
        Assert.That(packet.Groups[0].Outputs[0].Amount, Is.EqualTo(1000UL));
    }

    [Test]
    public void Build_MultipleAssets_CreatesMultipleGroups()
    {
        var inputs = new (string, ushort, ulong)[]
        {
            (FakeAssetId, 0, 500),
            (FakeAssetId2, 1, 300)
        };
        var result = AssetPacketBuilder.Build(inputs, null, changeVout: 0);

        Assert.That(result, Is.Not.Null);
        var packet = Packet.FromScript(result!.ScriptPubKey);
        Assert.That(packet.Groups, Has.Count.EqualTo(2));
    }

    [Test]
    public void Build_NoUnderflow_WhenOutputsExceedInputs()
    {
        // Asset only in outputs (no inputs) — should not underflow
        var inputs = Array.Empty<(string, ushort, ulong)>();
        var outputs = new[] { (FakeAssetId, (ushort)0, 500UL) };
        var result = AssetPacketBuilder.Build(inputs, outputs, changeVout: 0);

        // No inputs means null (no asset packet)
        Assert.That(result, Is.Null);
    }
}

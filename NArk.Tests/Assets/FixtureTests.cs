using System.Text;
using System.Text.Json;
using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Tests.Assets;

/// <summary>
/// Tests asset serialization against ts-sdk JSON fixture vectors.
/// Fixtures sourced from https://github.com/arkade-os/ts-sdk/tree/master/test/fixtures/
/// </summary>
[TestFixture]
public class FixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Assets", "Fixtures", name);

    private static JsonElement LoadFixture(string name)
    {
        var json = File.ReadAllText(FixturePath(name));
        return JsonDocument.Parse(json).RootElement;
    }

    #region AssetId Fixtures

    [Test]
    public void AssetId_ValidFixtures_MatchSerialization()
    {
        var fixture = LoadFixture("asset_id_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var txid = tc.GetProperty("txid").GetString()!;
            var index = (ushort)tc.GetProperty("index").GetInt32();
            var expectedHex = tc.GetProperty("serializedHex").GetString()!;

            var assetId = AssetId.Create(txid, index);
            var serialized = Convert.ToHexString(assetId.Serialize()).ToLowerInvariant();
            Assert.That(serialized, Is.EqualTo(expectedHex), $"AssetId fixture '{name}' serialization mismatch");

            // Round-trip
            var restored = AssetId.FromString(expectedHex);
            Assert.That(restored.GroupIndex, Is.EqualTo(index), $"AssetId fixture '{name}' round-trip index mismatch");
        }
    }

    #endregion

    #region AssetGroup Fixtures

    [Test]
    public void AssetGroup_ValidFixtures_MatchSerialization()
    {
        var fixture = LoadFixture("asset_group_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var expectedHex = tc.GetProperty("serializedHex").GetString()!;

            // Build the group from fixture data
            AssetId? assetId = null;
            if (tc.TryGetProperty("assetId", out var assetIdProp))
            {
                var txid = assetIdProp.GetProperty("txid").GetString()!;
                var index = (ushort)assetIdProp.GetProperty("index").GetInt32();
                assetId = AssetId.Create(txid, index);
            }

            AssetRef? controlAsset = null;
            if (tc.TryGetProperty("controlAsset", out var controlProp))
            {
                if (controlProp.TryGetProperty("groupIndex", out var gi))
                    controlAsset = AssetRef.FromGroupIndex((ushort)gi.GetInt32());
                else if (controlProp.TryGetProperty("assetId", out var caid))
                {
                    var ctxid = caid.GetProperty("txid").GetString()!;
                    var cidx = (ushort)caid.GetProperty("index").GetInt32();
                    controlAsset = AssetRef.FromId(AssetId.Create(ctxid, cidx));
                }
            }

            var inputs = new List<AssetInput>();
            if (tc.TryGetProperty("inputs", out var inputsArr))
            {
                foreach (var inp in inputsArr.EnumerateArray())
                {
                    var type = inp.GetProperty("type").GetString()!;
                    var vin = (ushort)inp.GetProperty("vin").GetInt32();
                    var amount = (ulong)inp.GetProperty("amount").GetInt64();
                    if (type == "local")
                        inputs.Add(AssetInput.Create(vin, amount));
                    else
                        inputs.Add(AssetInput.CreateIntent(inp.GetProperty("txid").GetString()!, vin, amount));
                }
            }

            var outputs = new List<AssetOutput>();
            if (tc.TryGetProperty("outputs", out var outputsArr))
            {
                foreach (var outp in outputsArr.EnumerateArray())
                {
                    var vout = (ushort)outp.GetProperty("vout").GetInt32();
                    var amount = (ulong)outp.GetProperty("amount").GetInt64();
                    outputs.Add(AssetOutput.Create(vout, amount));
                }
            }

            var metadata = new List<AssetMetadata>();
            if (tc.TryGetProperty("metadata", out var metaArr))
            {
                foreach (var m in metaArr.EnumerateArray())
                {
                    var key = m.GetProperty("key").GetString()!;
                    var value = m.GetProperty("value").GetString()!;
                    metadata.Add(AssetMetadata.Create(key, value));
                }
            }

            var group = AssetGroup.Create(assetId, controlAsset, inputs, outputs, metadata);
            var serialized = Convert.ToHexString(group.Serialize()).ToLowerInvariant();
            Assert.That(serialized, Is.EqualTo(expectedHex), $"AssetGroup fixture '{name}' serialization mismatch");

            // Round-trip: deserialize and re-serialize
            var reader = new BufferReader(Convert.FromHexString(expectedHex));
            var restored = AssetGroup.FromReader(reader);
            var reserialized = Convert.ToHexString(restored.Serialize()).ToLowerInvariant();
            Assert.That(reserialized, Is.EqualTo(expectedHex), $"AssetGroup fixture '{name}' round-trip mismatch");
        }
    }

    #endregion

    #region Packet Fixtures

    [Test]
    public void Packet_ValidFixtures_MatchSerialization()
    {
        var fixture = LoadFixture("packet_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("newPacket").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var expectedScript = tc.GetProperty("expectedScript").GetString()!;

            var groups = new List<AssetGroup>();
            foreach (var asset in tc.GetProperty("assets").EnumerateArray())
            {
                AssetRef? controlAsset = null;
                if (asset.TryGetProperty("controlAsset", out var ca))
                {
                    if (ca.TryGetProperty("groupIndex", out var gi))
                        controlAsset = AssetRef.FromGroupIndex((ushort)gi.GetInt32());
                }

                var outputs = new List<AssetOutput>();
                foreach (var outp in asset.GetProperty("outputs").EnumerateArray())
                    outputs.Add(AssetOutput.Create(
                        (ushort)outp.GetProperty("vout").GetInt32(),
                        (ulong)outp.GetProperty("amount").GetInt64()));

                var metadata = new List<AssetMetadata>();
                if (asset.TryGetProperty("metadata", out var metaArr))
                    foreach (var m in metaArr.EnumerateArray())
                        metadata.Add(AssetMetadata.Create(
                            m.GetProperty("key").GetString()!,
                            m.GetProperty("value").GetString()!));

                groups.Add(AssetGroup.Create(null, controlAsset, [], outputs, metadata));
            }

            var packet = Packet.Create(groups);
            var serialized = Convert.ToHexString(packet.Serialize()).ToLowerInvariant();
            Assert.That(serialized, Is.EqualTo(expectedScript), $"Packet fixture '{name}' serialization mismatch");

            // Round-trip: parse script and re-serialize
            var script = Script.FromBytesUnsafe(Convert.FromHexString(expectedScript));
            var restored = Packet.FromScript(script);
            var reserialized = Convert.ToHexString(restored.Serialize()).ToLowerInvariant();
            Assert.That(reserialized, Is.EqualTo(expectedScript), $"Packet fixture '{name}' round-trip mismatch");
        }
    }

    [Test]
    public void Packet_LeafTxPacket_MatchesFixture()
    {
        var fixture = LoadFixture("packet_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("leafTxPacket").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var scriptHex = tc.GetProperty("script").GetString()!;
            var intentTxid = tc.GetProperty("intentTxid").GetString()!;
            var expectedLeafHex = tc.GetProperty("expectedLeafTxPacket").GetString()!;

            var script = Script.FromBytesUnsafe(Convert.FromHexString(scriptHex));
            var packet = Packet.FromScript(script);
            var leafPacket = packet.LeafTxPacket(Convert.FromHexString(intentTxid));
            var leafSerialized = Convert.ToHexString(leafPacket.Serialize()).ToLowerInvariant();
            Assert.That(leafSerialized, Is.EqualTo(expectedLeafHex), $"LeafTxPacket fixture '{name}' mismatch");
        }
    }

    #endregion
}

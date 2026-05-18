using System.Linq;
using System.Text.Json;
using NArk.Core.Assets;

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
            var expected = tc.GetProperty("expected").GetString()!;

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
            var serialized = ToHex(packet.SerializePacketData());
            Assert.That(serialized, Is.EqualTo(expected), $"Packet fixture '{name}' serialization mismatch");

            // Round-trip: parse raw packet hex and re-serialize
            var restored = Packet.FromString(expected);
            var reserialized = ToHex(restored.SerializePacketData());
            Assert.That(reserialized, Is.EqualTo(expected), $"Packet fixture '{name}' round-trip mismatch");
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

            var packet = Packet.FromString(scriptHex);
            var leafPacket = packet.LeafTxPacket(Convert.FromHexString(intentTxid));
            var leafSerialized = ToHex(leafPacket.SerializePacketData());
            Assert.That(leafSerialized, Is.EqualTo(expectedLeafHex), $"LeafTxPacket fixture '{name}' mismatch");
        }
    }

    #endregion

    #region AssetRef Fixtures (cross-SDK, ts-sdk asset_ref_fixtures.json)

    [Test]
    public void AssetRef_ValidFromId_MatchSerialization()
    {
        var fixture = LoadFixture("asset_ref_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("newAssetRefFromId").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var txid = tc.GetProperty("txid").GetString()!;
            var index = unchecked((ushort)tc.GetProperty("index").GetInt64());
            var expected = tc.GetProperty("serializedHex").GetString()!;
            var refById = AssetRef.FromId(AssetId.Create(txid, index));
            Assert.That(refById.ToString(), Is.EqualTo(expected), $"AssetRef FromId '{name}' mismatch");
        }
    }

    [Test]
    public void AssetRef_ValidFromGroup_MatchSerialization()
    {
        var fixture = LoadFixture("asset_ref_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("newAssetRefFromGroup").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            // Fixtures intentionally include overflow (65536→0) / underflow
            // (-1→65535); an unchecked ushort cast reproduces the wrap the
            // vectors expect.
            var index = unchecked((ushort)tc.GetProperty("index").GetInt64());
            var expected = tc.GetProperty("serializedHex").GetString()!;
            Assert.That(AssetRef.FromGroupIndex(index).ToString(), Is.EqualTo(expected),
                $"AssetRef FromGroup '{name}' mismatch");
        }
    }

    [Test]
    public void AssetRef_InvalidFromString_AreRejected()
    {
        var fixture = LoadFixture("asset_ref_fixtures.json");
        foreach (var tc in fixture.GetProperty("invalid").GetProperty("newAssetRefFromString").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("serializedHex").GetString()!;
            // The contract is "this vector is rejected" — assert the parse
            // pipeline throws (FormatException for non-hex, or AssetRef's
            // own validation). We don't pin exact messages: those are
            // implementation detail and differ across SDKs.
            Assert.That(() => AssetRef.FromBytes(Convert.FromHexString(hex)),
                Throws.Exception, $"AssetRef invalid '{name}' should be rejected");
        }
    }

    #endregion

    #region AssetInput Fixtures (cross-SDK, ts-sdk asset_input_fixtures.json)

    [Test]
    public void AssetInput_ValidNewInput_MatchSerialization()
    {
        var fixture = LoadFixture("asset_input_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("newInput").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var type = tc.GetProperty("type").GetString()!;
            var vin = (ushort)tc.GetProperty("vin").GetInt64();
            var amount = (ulong)tc.GetProperty("amount").GetInt64();
            var expected = tc.GetProperty("serializedHex").GetString()!;
            var input = type == "intent"
                ? AssetInput.CreateIntent(tc.GetProperty("txid").GetString()!, vin, amount)
                : AssetInput.Create(vin, amount);
            Assert.That(input.ToString(), Is.EqualTo(expected), $"AssetInput '{name}' mismatch");
        }
    }

    [Test]
    public void AssetInput_InvalidFromString_AreRejected()
    {
        var fixture = LoadFixture("asset_input_fixtures.json");
        foreach (var tc in fixture.GetProperty("invalid").GetProperty("newInputFromString").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("serializedHex").GetString()!;
            Assert.That(() => AssetInput.FromReader(new BufferReader(Convert.FromHexString(hex))),
                Throws.Exception, $"AssetInput invalid '{name}' should be rejected");
        }
    }

    #endregion

    #region AssetOutput Fixtures (cross-SDK, ts-sdk asset_output_fixtures.json)

    [Test]
    public void AssetOutput_ValidNewOutput_MatchSerialization()
    {
        var fixture = LoadFixture("asset_output_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("newOutput").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var vout = (ushort)tc.GetProperty("vout").GetInt64();
            var amount = (ulong)tc.GetProperty("amount").GetInt64();
            var expected = tc.GetProperty("serializedHex").GetString()!;
            Assert.That(AssetOutput.Create(vout, amount).ToString(), Is.EqualTo(expected),
                $"AssetOutput '{name}' mismatch");
        }
    }

    [Test]
    public void AssetOutput_InvalidFromString_AreRejected()
    {
        var fixture = LoadFixture("asset_output_fixtures.json");
        foreach (var tc in fixture.GetProperty("invalid").GetProperty("newOutputFromString").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("serializedHex").GetString()!;
            Assert.That(() => AssetOutput.FromReader(new BufferReader(Convert.FromHexString(hex))),
                Throws.Exception, $"AssetOutput invalid '{name}' should be rejected");
        }
    }

    #endregion

    #region Metadata Fixtures (cross-SDK, ts-sdk metadata_fixtures.json)

    [Test]
    public void Metadata_ValidNewMetadata_MatchSerialization()
    {
        var fixture = LoadFixture("metadata_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("newMetadata").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var key = tc.GetProperty("key").GetString()!;
            var value = tc.GetProperty("value").GetString()!;
            var expected = tc.GetProperty("serializedHex").GetString()!;
            Assert.That(AssetMetadata.Create(key, value).ToString(), Is.EqualTo(expected),
                $"Metadata '{name}' mismatch");
        }
    }

    [Test]
    public void Metadata_HashVectors_MatchCrossSdk()
    {
        var fixture = LoadFixture("metadata_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("hash").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var items = tc.GetProperty("metadata").EnumerateArray()
                .Select(m => AssetMetadata.Create(
                    m.GetProperty("key").GetString()!, m.GetProperty("value").GetString()!))
                .ToList();
            var expected = tc.GetProperty("expectedHash").GetString()!;
            // The Merkle hash is the strongest cross-SDK conformance check:
            // any spec-compliant SDK must produce identical roots.
            Assert.That(ToHex(new MetadataList(items).Hash()), Is.EqualTo(expected),
                $"MetadataList hash '{name}' mismatch");
        }
    }

    [Test]
    public void Metadata_InvalidNewMetadata_AreRejected()
    {
        var fixture = LoadFixture("metadata_fixtures.json");
        foreach (var tc in fixture.GetProperty("invalid").GetProperty("newMetadata").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var key = tc.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            var value = tc.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            Assert.That(() => AssetMetadata.Create(key, value),
                Throws.Exception, $"Metadata invalid '{name}' should be rejected");
        }
    }

    #endregion

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

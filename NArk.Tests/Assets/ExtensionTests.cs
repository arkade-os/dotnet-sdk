using System.Text.Json;
using NArk.Core.Assets;
using NBitcoin;
using NBitcoin.Protocol;

namespace NArk.Tests.Assets;

[TestFixture]
public class ExtensionTests
{
    private static string FixturePath(string name) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Assets", "Fixtures", name);

    private static JsonElement LoadFixture(string name)
    {
        var json = File.ReadAllText(FixturePath(name));
        return JsonDocument.Parse(json).RootElement;
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Test]
    public void Valid_NewExtensionFromBytes()
    {
        var fixture = LoadFixture("extension_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("newExtensionFromBytes").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("hex").GetString()!;
            var expectedCount = tc.GetProperty("expectedPacketCount").GetInt32();

            var data = Convert.FromHexString(hex);
            var script = new Script(data);
            var ext = Extension.FromScript(script);

            Assert.That(ext.Packets, Has.Count.EqualTo(expectedCount), $"'{name}' packet count mismatch");

            if (tc.TryGetProperty("expectedPacketTypes", out var types))
            {
                var expectedTypes = types.EnumerateArray().Select(t => (byte)t.GetInt32()).ToList();
                for (var i = 0; i < expectedTypes.Count; i++)
                    Assert.That(ext.Packets[i].PacketType, Is.EqualTo(expectedTypes[i]),
                        $"'{name}' packet[{i}] type mismatch");
            }
        }
    }

    [Test]
    public void Valid_Roundtrip()
    {
        var fixture = LoadFixture("extension_fixtures.json");
        foreach (var tc in fixture.GetProperty("valid").GetProperty("roundtrip").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("hex").GetString()!;

            var data = Convert.FromHexString(hex);
            var script = new Script(data);
            var ext = Extension.FromScript(script);

            var serialized = ext.Serialize();
            Assert.That(ToHex(serialized), Is.EqualTo(hex), $"'{name}' roundtrip mismatch");
            Assert.That(Extension.IsExtension(new Script(serialized)), Is.True,
                $"'{name}' IsExtension should be true after roundtrip");

            var txOut = ext.ToTxOut();
            Assert.That(txOut, Is.Not.Null, $"'{name}' TxOut should not be null");
            Assert.That(ToHex(txOut.ScriptPubKey.ToBytes()), Is.EqualTo(hex),
                $"'{name}' TxOut script mismatch");
        }
    }

    [Test]
    public void IsExtension_True()
    {
        var fixture = LoadFixture("extension_fixtures.json");
        foreach (var tc in fixture.GetProperty("isExtension").GetProperty("true").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("hex").GetString()!;
            var data = Convert.FromHexString(hex);
            Assert.That(Extension.IsExtension(new Script(data)), Is.True, $"'{name}' should be extension");
        }
    }

    [Test]
    public void IsExtension_False()
    {
        var fixture = LoadFixture("extension_fixtures.json");
        foreach (var tc in fixture.GetProperty("isExtension").GetProperty("false").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("hex").GetString()!;
            var data = hex.Length > 0 ? Convert.FromHexString(hex) : [];
            Assert.That(Extension.IsExtension(new Script(data)), Is.False, $"'{name}' should not be extension");
        }
    }

    [Test]
    public void Invalid_NewExtensionFromBytes()
    {
        var fixture = LoadFixture("extension_fixtures.json");
        foreach (var tc in fixture.GetProperty("invalid").GetProperty("newExtensionFromBytes").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("hex").GetString()!;
            var expectedError = tc.GetProperty("expectedError").GetString()!;

            var data = hex.Length > 0 ? Convert.FromHexString(hex) : [];
            var script = new Script(data);

            var ex = Assert.Throws<ArgumentException>(() => Extension.FromScript(script));
            Assert.That(ex!.Message, Does.Contain(expectedError),
                $"'{name}' error mismatch: got '{ex.Message}'");
        }
    }

    [Test]
    public void NewExtensionFromTx_Valid()
    {
        var fixture = LoadFixture("extension_fixtures.json");
        foreach (var tc in fixture.GetProperty("newExtensionFromTx").GetProperty("valid").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("hex").GetString()!;
            var expectedCount = tc.GetProperty("expectedPacketCount").GetInt32();

            var tx = ParseTxNoWitness(hex);
            var ext = Extension.FromTransaction(tx);

            Assert.That(ext, Is.Not.Null, $"'{name}' should find extension in tx");
            Assert.That(ext!.Packets, Has.Count.EqualTo(expectedCount),
                $"'{name}' packet count mismatch");
        }
    }

    [Test]
    public void NewExtensionFromTx_Invalid()
    {
        var fixture = LoadFixture("extension_fixtures.json");
        foreach (var tc in fixture.GetProperty("newExtensionFromTx").GetProperty("invalid").EnumerateArray())
        {
            var name = tc.GetProperty("name").GetString()!;
            var hex = tc.GetProperty("hex").GetString()!;
            var expectedError = tc.GetProperty("expectedError").GetString()!;

            var tx = ParseTxNoWitness(hex);

            if (expectedError == "ErrExtensionNotFound")
            {
                var ext = Extension.FromTransaction(tx);
                Assert.That(ext, Is.Null, $"'{name}' should not find extension in tx");
            }
            else
            {
                Assert.Throws<ArgumentException>(() =>
                {
                    var ext = Extension.FromTransaction(tx);
                    // If FromTransaction returned null, also valid for "not found"
                    if (ext is null)
                        throw new ArgumentException(expectedError);
                });
            }
        }
    }

    /// <summary>
    /// Parse a raw tx hex without SegWit interpretation.
    /// Go's btcutil treats 0-input txs as legacy, whereas NBitcoin interprets
    /// the 0x0001 bytes as a SegWit marker/flag, causing parse failures.
    /// </summary>
    private static Transaction ParseTxNoWitness(string hex)
    {
        var data = Convert.FromHexString(hex);
        var tx = Network.Main.CreateTransaction();
        var bs = new BitcoinStream(data) { ConsensusFactory = Network.Main.Consensus.ConsensusFactory };
        bs.TransactionOptions &= ~TransactionOptions.Witness;
        tx.ReadWrite(bs);
        return tx;
    }

    [Test]
    public void GetAssetPacket_ReturnsPacketWhenPresent()
    {
        var group = AssetGroup.Create(null, AssetRef.FromGroupIndex(0), [],
            [AssetOutput.Create(0, 100)], []);
        var packet = Packet.Create([group]);
        var ext = new Extension([packet]);

        Assert.That(ext.GetAssetPacket(), Is.Not.Null);
        Assert.That(ext.GetAssetPacket()!.Groups, Has.Count.EqualTo(1));
    }

    [Test]
    public void GetAssetPacket_ReturnsNullWhenAbsent()
    {
        var ext = new Extension([new UnknownPacket(0xFF, [0xDE, 0xAD])]);
        Assert.That(ext.GetAssetPacket(), Is.Null);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.Arkade.Program;

namespace NArk.Tests.Arkade;

[TestFixture]
public class ArkadeArtifactRoundTripTests
{
    private static readonly ArkadeArtifactParser Parser = new();

    [Test]
    public void FullArtifact_RoundTrips_Structurally()
    {
        const string json = """
        {
            "version": 0,
            "params": ["hash", "receiver"],
            "functions": {
                "claim": {
                    "inputs": [{ "name": "preimage", "type": "bytes" }],
                    "tapscript": {
                        "signers": ["server"],
                        "asm": ["HASH160", "$hash", "EQUALVERIFY"],
                        "witness": ["preimage"],
                        "csv": { "type": "blocks", "value": "144" }
                    },
                    "arkadeScript": {
                        "asm": ["OP_TXID", "0xdeadbeef", "$hash"],
                        "witness": [0]
                    }
                },
                "refund": {
                    "tapscript": {
                        "signers": ["server", "user"],
                        "cltv": "500000"
                    }
                }
            }
        }
        """;

        var program = Parser.ParseArtifact(JsonNode.Parse(json)!.AsObject());
        var reserialized = ArkadeArtifactSerializer.SerializeArtifact(program);
        var reparsed = Parser.ParseArtifact(reserialized);

        AssertProgramsEqual(program, reparsed);
    }

    [Test]
    public void SecondsCsv_RoundTrips()
    {
        const string json = """
        {
            "version": 0,
            "functions": {
                "refund": {
                    "tapscript": {
                        "signers": ["server"],
                        "csv": { "type": "seconds", "value": "1024" }
                    }
                }
            }
        }
        """;

        var program = Parser.ParseArtifact(JsonNode.Parse(json)!.AsObject());
        var reparsed = Parser.ParseArtifact(ArkadeArtifactSerializer.SerializeArtifact(program));

        var original = program.Functions["refund"].Tapscript.Csv!.Value;
        var roundTripped = reparsed.Functions["refund"].Tapscript.Csv!.Value;
        Assert.That(roundTripped.LockPeriod, Is.EqualTo(original.LockPeriod));
    }

    private static void AssertProgramsEqual(ArkadeProgram a, ArkadeProgram b)
    {
        var optionsIndented = new JsonSerializerOptions { WriteIndented = false };
        var aJson = ArkadeArtifactSerializer.SerializeArtifact(a).ToJsonString(optionsIndented);
        var bJson = ArkadeArtifactSerializer.SerializeArtifact(b).ToJsonString(optionsIndented);
        Assert.That(bJson, Is.EqualTo(aJson));
    }
}

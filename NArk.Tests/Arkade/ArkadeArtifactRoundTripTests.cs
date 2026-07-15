using System.Numerics;
using System.Text.Json.Nodes;
using NArk.Arkade.Program;
using NArk.Arkade.Scripts;
using NBitcoin;

namespace NArk.Tests.Arkade;

/// <summary>
/// Round-trips artifact JSON through <see cref="ArkadeArtifactParser"/> →
/// <see cref="ArkadeArtifactSerializer"/> → parser again, asserting on the resulting
/// <em>values</em> rather than only on serialize(a) == serialize(b). A self-consistent
/// structural comparison can't catch a field that both serialize passes drop; value
/// assertions can.
/// </summary>
[TestFixture]
public class ArkadeArtifactRoundTripTests
{
    private static readonly ArkadeArtifactParser Parser = new();

    private static ArkadeProgram RoundTrip(string json)
    {
        var program = Parser.ParseArtifact(JsonNode.Parse(json)!.AsObject());
        return Parser.ParseArtifact(ArkadeArtifactSerializer.SerializeArtifact(program));
    }

    [Test]
    public void FullArtifact_PreservesEveryField()
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

        var program = RoundTrip(json);

        Assert.That(program.Version, Is.EqualTo(ArkadeProgram.SupportedVersion));
        Assert.That(program.Params!.Select(p => p.Name), Is.EqualTo(new[] { "hash", "receiver" }));
        Assert.That(program.Params!.All(p => p.Type is null), Is.True); // bare strings stay untyped
        Assert.That(program.Functions.Keys, Is.EqualTo(new[] { "claim", "refund" }));

        var claim = program.Functions["claim"];
        Assert.That(claim.Inputs!.Single().Name, Is.EqualTo("preimage"));
        Assert.That(claim.Inputs!.Single().Type, Is.EqualTo(InputType.Bytes));
        Assert.That(claim.Tapscript.Signers, Is.EqualTo(new[] { AsmToken.FromText("server") }));
        Assert.That(claim.Tapscript.Asm, Is.EqualTo(new AsmToken[] { "HASH160", "$hash", "EQUALVERIFY" }));
        Assert.That(claim.Tapscript.Witness, Is.EqualTo(new AsmToken[] { "preimage" }));
        Assert.That(claim.Tapscript.Csv!.Value.Value & 0x0000FFFF, Is.EqualTo(144));
        Assert.That(claim.Tapscript.Cltv, Is.Null);

        Assert.That(claim.ScriptSegment!.Asm, Is.EqualTo(new AsmToken[]
            { "OP_TXID", Convert.FromHexString("deadbeef"), "$hash" }));
        Assert.That(claim.ScriptSegment!.Witness, Is.EqualTo(new AsmToken[] { 0 }));

        var refund = program.Functions["refund"];
        Assert.That(refund.Tapscript.Signers, Is.EqualTo(new AsmToken[] { "server", "user" }));
        Assert.That(refund.Tapscript.Cltv!.Value.Value, Is.EqualTo(500_000));
        Assert.That(refund.Tapscript.Csv, Is.Null);
    }

    [Test]
    public void Name_RoundTrips()
    {
        var named = RoundTrip("""
        { "version": 0, "name": "htlc", "functions": { "f": { "tapscript": { "signers": ["server"] } } } }
        """);
        Assert.That(named.Name, Is.EqualTo("htlc"));

        var unnamed = RoundTrip("""
        { "version": 0, "functions": { "f": { "tapscript": { "signers": ["server"] } } } }
        """);
        Assert.That(unnamed.Name, Is.Null);
    }

    [Test]
    public void Cltv_ValueSurvives()
    {
        var program = RoundTrip("""
        { "version": 0, "functions": { "f": { "tapscript": { "signers": ["server"], "cltv": "500000" } } } }
        """);
        Assert.That(program.Functions["f"].Tapscript.Cltv!.Value.Value, Is.EqualTo(500_000));
    }

    [Test]
    public void BlocksCsv_ValueSurvives()
    {
        var program = RoundTrip("""
        { "version": 0, "functions": { "f": { "tapscript": { "signers": ["server"], "csv": { "type": "blocks", "value": "144" } } } } }
        """);
        var csv = program.Functions["f"].Tapscript.Csv!.Value;
        Assert.That(csv.LockType, Is.Not.EqualTo(SequenceLockType.Time)); // blocks, not seconds
        Assert.That(csv.Value & 0x0000FFFF, Is.EqualTo(144));
    }

    [Test]
    public void SecondsCsv_ValueSurvives()
    {
        var program = RoundTrip("""
        { "version": 0, "functions": { "f": { "tapscript": { "signers": ["server"], "csv": { "type": "seconds", "value": "1024" } } } } }
        """);
        var csv = program.Functions["f"].Tapscript.Csv!.Value;
        Assert.That(csv.LockType, Is.EqualTo(SequenceLockType.Time));
        Assert.That((long)csv.LockPeriod.TotalSeconds, Is.EqualTo(1024));
    }

    [Test]
    public void BytesToken_SurvivesExactly()
    {
        var program = RoundTrip("""
        { "version": 0, "functions": { "f": { "tapscript": { "signers": ["server"], "asm": ["0xdeadbeef"] } } } }
        """);
        var token = program.Functions["f"].Tapscript.Asm!.Single();
        Assert.That(token.Kind, Is.EqualTo(AsmTokenKind.Bytes));
        Assert.That(token.Bytes, Is.EqualTo(Convert.FromHexString("deadbeef")));
    }

    [Test]
    public void SmallAndLargeNumberTokens_Survive()
    {
        // A small int stays a JSON number and round-trips as a Number token. A value beyond
        // the JSON-safe range (2^53) is serialized as 0x-hex, which the parser reads back as a
        // Bytes token: the *kind* changes but the value's script-num encoding is preserved, so
        // it compiles to the same push. (Hex on the wire is indistinguishable from raw bytes.)
        var big = (BigInteger.One << 60) + 7; // 1152921504606846983
        var program = RoundTrip($$"""
        { "version": 0, "functions": { "f": { "tapscript": { "signers": ["server"], "asm": [0, {{big}}] } } } }
        """);
        var asm = program.Functions["f"].Tapscript.Asm!;

        Assert.That(asm[0].Kind, Is.EqualTo(AsmTokenKind.Number));
        Assert.That(asm[0].Number, Is.EqualTo(BigInteger.Zero));

        Assert.That(asm[1].Kind, Is.EqualTo(AsmTokenKind.Bytes));
        Assert.That(asm[1].Bytes, Is.EqualTo(ArkadeScriptNum.Encode(big)));
    }

    [Test]
    public void UntypedInput_And_NoParams_Survive()
    {
        var program = RoundTrip("""
        { "version": 0, "functions": { "f": { "inputs": ["x"], "tapscript": { "signers": ["server"] } } } }
        """);
        Assert.That(program.Params, Is.Null);
        var input = program.Functions["f"].Inputs!.Single();
        Assert.That(input.Name, Is.EqualTo("x"));
        Assert.That(input.Type, Is.Null);
    }

    [Test]
    public void EveryInputType_RoundTrips()
    {
        foreach (var (wire, expected) in new[]
        {
            ("bytes", InputType.Bytes), ("pubkey", InputType.Pubkey),
            ("sig", InputType.Sig), ("hash", InputType.Hash), ("int", InputType.Int),
        })
        {
            var program = RoundTrip($$"""
            { "version": 0, "functions": { "f": { "inputs": [{ "name": "a", "type": "{{wire}}" }], "tapscript": { "signers": ["server"] } } } }
            """);
            Assert.That(program.Functions["f"].Inputs!.Single().Type, Is.EqualTo(expected), $"type '{wire}'");
        }
    }

    [Test]
    public void CovenantWithoutWitness_Survives()
    {
        var program = RoundTrip("""
        { "version": 0, "functions": { "f": { "tapscript": { "signers": ["server"] }, "arkadeScript": { "asm": ["OP_TXID"] } } } }
        """);
        var covenant = program.Functions["f"].ScriptSegment!;
        Assert.That(covenant.Asm, Is.EqualTo(new AsmToken[] { "OP_TXID" }));
        Assert.That(covenant.Witness, Is.Null);
    }
}

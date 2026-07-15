using System.Numerics;
using System.Text.Json.Nodes;
using NArk.Arkade.Scripts;
using NBitcoin;

namespace NArk.Arkade.Program;

/// <summary>
/// Serializes an <see cref="ArkadeProgram"/> back into artifact JSON — the inverse of
/// <see cref="ArkadeArtifactParser"/>. Mirrors the ts-sdk's <c>stringifyArtifact</c>.
/// </summary>
public static class ArkadeArtifactSerializer
{
    /// <summary>JS's <c>Number.MAX_SAFE_INTEGER</c> (2^53 - 1) — the ts-sdk's cutoff for emitting a plain JSON number vs. a hex-encoded minimal script-num.</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>Serialize a full <see cref="ArkadeProgram"/> to its artifact JSON form.</summary>
    public static JsonObject SerializeArtifact(ArkadeProgram program)
    {
        var functions = new JsonObject();
        foreach (var (name, fn) in program.Functions)
        {
            functions[name] = SerializeFunction(fn);
        }

        var root = new JsonObject
        {
            ["version"] = program.Version,
            ["functions"] = functions,
        };
        if (program.Name is { } programName)
        {
            root["name"] = programName;
        }
        if (program.Params is { Count: > 0 } prms)
        {
            root["params"] = new JsonArray(prms.Select(SerializeInput).ToArray());
        }
        return root;
    }

    private static JsonObject SerializeFunction(ArkadeFunction function)
    {
        var obj = new JsonObject { ["tapscript"] = SerializeTapscript(function.Tapscript) };
        if (function.Inputs is { Count: > 0 } inputs)
        {
            obj["inputs"] = new JsonArray(inputs.Select(SerializeInput).ToArray());
        }
        if (function.ScriptSegment is { } covenant)
        {
            obj["arkadeScript"] = SerializeCovenant(covenant);
        }
        return obj;
    }

    private static JsonNode SerializeInput(TypedInput programInput)
    {
        if (programInput.Type is null) return programInput.Name;
        return new JsonObject { ["name"] = programInput.Name, ["type"] = SerializeArgType(programInput.Type.Value) };
    }

    private static string SerializeArgType(InputType type) => type switch
    {
        InputType.Bytes => "bytes",
        InputType.Pubkey => "pubkey",
        InputType.Sig => "sig",
        InputType.Hash => "hash",
        InputType.Int => "int",
        _ => throw new InvalidOperationException($"Unknown arg type '{type}'."),
    };

    private static JsonObject SerializeTapscript(TapscriptSegment seg)
    {
        var obj = new JsonObject { ["signers"] = SerializeTokenList(seg.Signers) };
        if (seg.Asm is { } asm) obj["asm"] = SerializeTokenList(asm);
        if (seg.Witness is { } witness) obj["witness"] = SerializeTokenList(witness);
        if (seg.Csv is { } csv) obj["csv"] = SerializeSequence(csv);
        if (seg.Cltv is { } cltv) obj["cltv"] = cltv.Value.ToString();
        return obj;
    }

    private static JsonObject SerializeSequence(Sequence csv)
    {
        if (csv.LockType == SequenceLockType.Time)
        {
            return new JsonObject
            {
                ["type"] = "seconds",
                ["value"] = ((long)csv.LockPeriod.TotalSeconds).ToString(),
            };
        }

        return new JsonObject
        {
            ["type"] = "blocks",
            ["value"] = (csv.Value & 0x0000FFFF).ToString(),
        };
    }

    private static JsonObject SerializeCovenant(ArkadeScriptSegment seg)
    {
        var obj = new JsonObject { ["asm"] = SerializeTokenList(seg.Asm) };
        if (seg.Witness is { } witness) obj["witness"] = SerializeTokenList(witness);
        return obj;
    }

    private static JsonArray SerializeTokenList(IEnumerable<AsmToken> tokens)
        => new(tokens.Select(SerializeToken).ToArray());

    private static JsonNode SerializeToken(AsmToken token) => token.Kind switch
    {
        AsmTokenKind.Bytes => "0x" + Convert.ToHexString(token.Bytes!).ToLowerInvariant(),
        AsmTokenKind.Text => token.Text!,
        AsmTokenKind.Number => SerializeNumber(token.Number!.Value),
        _ => throw new InvalidOperationException("Unknown token kind."),
    };

    private static JsonNode SerializeNumber(BigInteger value)
        => value >= -MaxSafeInteger && value <= MaxSafeInteger
            ? (long)value
            : "0x" + Convert.ToHexString(ArkadeScriptNum.Encode(value)).ToLowerInvariant();
}

using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.Arkade.Program.Models;
using NBitcoin;

namespace NArk.Arkade.Program;

/// <summary>
/// Parses an Arkade Program compiler artifact (JSON) into a typed <see cref="ArkadeProgram"/>.
/// Mirrors the ts-sdk's <c>parseArtifact</c> — a pure structural conversion. It does not
/// validate signer/timelock exclusivity, resolve <c>$param</c> placeholders, or compile
/// anything to script bytes; those are later, separate steps.
/// </summary>
public class ArkadeArtifactParser
{
    /// <summary>Parse a full artifact document into an <see cref="ArkadeProgram"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The artifact is missing required fields, or its <c>version</c> is not
    /// <see cref="ArkadeProgram.SupportedVersion"/>.
    /// </exception>
    public ArkadeProgram ParseArtifact(JsonObject json)
    {
        var version = json["version"]?.GetValue<int>() ?? 0;
        if (version != ArkadeProgram.SupportedVersion)
        {
            throw new InvalidOperationException($"Artifact version {version} is not supported.");
        }

        var functions = json["functions"]?.AsObject()
                         ?? throw new InvalidOperationException("Artifact is missing 'functions'.");

        return new ArkadeProgram
        {
            Version = version,
            Name = json["name"]?.GetValue<string>(),
            Params = ParseInputs(json["params"]),
            Functions = ParseFunctions(functions),
        };
    }

    private Dictionary<string, ArkadeFunction> ParseFunctions(JsonObject functionsNode)
    {
        var functions = new Dictionary<string, ArkadeFunction>();
        foreach (var (functionName, functionDefinition) in functionsNode)
        {
            if (functionDefinition is null) continue;
            functions.Add(functionName, ParseFunction(functionDefinition));
        }
        return functions;
    }

    private ArkadeFunction ParseFunction(JsonNode function)
    {
        var tapscriptNode = function["tapscript"]?.AsObject()
                            ?? throw new InvalidOperationException("Function is missing required 'tapscript'.");

        return new ArkadeFunction
        {
            Inputs = ParseInputs(function["inputs"]),
            Tapscript = ParseTapscriptSegment(tapscriptNode),
            ScriptSegment = ParseArkadeScriptSegment(function["arkadeScript"]?.AsObject()),
        };
    }

    private TapscriptSegment ParseTapscriptSegment(JsonObject tapscript)
    {
        var (csv, cltv) = ParseTimelocks(tapscript);

        return new TapscriptSegment
        {
            Signers = ParseTokenList(tapscript["signers"]),
            Asm = tapscript["asm"] is { } asm ? ParseTokenList(asm) : null,
            Witness = tapscript["witness"] is { } witness ? ParseTokenList(witness) : null,
            Csv = csv,
            Cltv = cltv,
        };
    }

    private ArkadeScriptSegment? ParseArkadeScriptSegment(JsonObject? arkadeScript)
    {
        if (arkadeScript is null) return null;

        return new ArkadeScriptSegment
        {
            Asm = ParseTokenList(arkadeScript["asm"]),
            Witness = arkadeScript["witness"] is { } witness ? ParseTokenList(witness) : null,
        };
    }
    
    private static (Sequence? Csv, LockTime? Cltv) ParseTimelocks(JsonObject tapscript)
    {
        if (tapscript["cltv"] is { } cltvNode)
        {
            return (null, new LockTime(uint.Parse(cltvNode.GetValue<string>())));
        }

        if (tapscript["csv"]?.AsObject() is not { } csvNode)
        {
            return (null, null);
        }

        var type = csvNode["type"]?.GetValue<string>()
                   ?? throw new InvalidOperationException("csv.type is required.");
        var value = uint.Parse(csvNode["value"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("csv.value is required."));

        var csv = type switch
        {
            "blocks" => new Sequence(value),
            "seconds" => new Sequence(TimeSpan.FromSeconds(value)),
            _ => throw new InvalidOperationException($"Unknown csv type '{type}'."),
        };
        return (csv, null);
    }

    private static List<TypedInput>? ParseInputs(JsonNode? inputsNode)
    {
        var arr = inputsNode?.AsArray();
        if (arr is null) return null;

        return arr.Select(x =>
        {
            var node = x ?? throw new InvalidOperationException("'inputs' contains a null entry.");
            if (node.GetValueKind() == JsonValueKind.String)
            {
                return new TypedInput { Name = node.GetValue<string>() };
            }

            var obj = node.AsObject();
            var name = obj["name"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("Input descriptor is missing 'name'.");
            var type = obj["type"]?.GetValue<string>() is { } t ? ParseArgType(t) : (InputType?)null;
            return new TypedInput { Name = name, Type = type };
        }).ToList();
    }

    private static InputType ParseArgType(string s) => s switch
    {
        "bytes" => InputType.Bytes,
        "pubkey" => InputType.Pubkey,
        "sig" => InputType.Sig,
        "hash" => InputType.Hash,
        "int" => InputType.Int,
        _ => throw new InvalidOperationException($"Unknown input type '{s}'."),
    };

    private static List<AsmToken> ParseTokenList(JsonNode? node)
    {
        var arr = node?.AsArray();
        if (arr is null) return [];

        return arr.Select(x =>
            ParseToken(x ?? throw new InvalidOperationException("Token array contains a null entry."))
        ).ToList();
    }

    private static AsmToken ParseToken(JsonNode node)
    {
        if (node.GetValueKind() == JsonValueKind.Number)
            return AsmToken.FromNumber(node.GetValue<long>());

        var s = node.GetValue<string>();
        return s.StartsWith("0x", StringComparison.Ordinal)
            ? AsmToken.FromBytes(Convert.FromHexString(s[2..]))
            : AsmToken.FromText(s);
    }
}

using System.Numerics;
using System.Text.Json.Nodes;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Program;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Contracts;

/// <summary>
/// An <see cref="ArkContract"/> whose spending paths come from a compiled
/// <see cref="ArkadeProgram"/> rather than a hardcoded script shape. Compiles once at
/// construction; <see cref="GetScriptBuilders"/> hands the compiled leaves to the base
/// class, which assembles them into a taproot tree exactly like every other contract
/// (<see cref="ArkContract.GetTaprootSpendInfo"/>).
/// </summary>
public sealed class ArkProgramContract : ArkContract
{
    /// <summary>The contract type discriminator used in <see cref="ArkContract.Type"/> and persistence.</summary>
    public const string ContractType = "ArkadeProgram";

    private readonly ArkadeProgram _program;
    private readonly IReadOnlyDictionary<string, AsmToken> _args;
    private readonly OutputDescriptor? _user;
    private readonly ECXOnlyPubKey? _emulatorKey;
    private readonly IReadOnlyList<CompiledArkadeFunction> _compiled;

    public ArkProgramContract(
        OutputDescriptor server,
        ArkadeProgram program,
        IReadOnlyDictionary<string, AsmToken> args,
        OutputDescriptor? user = null,
        ECXOnlyPubKey? emulatorKey = null)
        : base(server)
    {
        _program = program;
        _args = args;
        _user = user;
        _emulatorKey = emulatorKey;

        var keys = new ArkadeProgramKeys
        {
            ServerKey = server.ToXOnlyPubKey(),
            UserKey = user?.ToXOnlyPubKey(),
            EmulatorKey = emulatorKey,
        };
        _compiled = ArkadeProgramCompiler.Compile(program, args, keys);
    }

    public override string Type => ContractType;

    /// <summary>Output descriptor for the wallet's own key, if the program names a <c>"user"</c> signer.</summary>
    public OutputDescriptor? User => _user;

    /// <summary>Args this program was compiled against.</summary>
    public IReadOnlyDictionary<string, AsmToken> Args => _args;

    /// <summary>All compiled spending paths, in declaration order.</summary>
    public IReadOnlyList<CompiledArkadeFunction> CompiledFunctions => _compiled;

    /// <summary>The compiled spending path with the given function name, if any.</summary>
    public CompiledArkadeFunction? FunctionByName(string name)
        => _compiled.FirstOrDefault(f => f.Name == name);

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
        => _compiled.Select(f => f.ToScriptBuilder());

    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["server"] = Server!.ToString(),
            ["program"] = ArkadeArtifactSerializer.SerializeArtifact(_program).ToJsonString(),
            ["args"] = SerializeArgsMap(_args),
        };
        if (_user is not null) data["user"] = _user.ToString();
        if (_emulatorKey is not null) data["emulator"] = Convert.ToHexString(_emulatorKey.ToBytes()).ToLowerInvariant();
        return data;
    }

    /// <summary>Rebuilds a contract from persisted <see cref="ArkContract.GetContractData"/> output.</summary>
    public static ArkProgramContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var program = new ArkadeArtifactParser().ParseArtifact(JsonNode.Parse(contractData["program"])!.AsObject());
        var args = contractData.TryGetValue("args", out var argsJson)
            ? DeserializeArgsMap(argsJson)
            : new Dictionary<string, AsmToken>();
        var user = contractData.TryGetValue("user", out var userStr)
            ? KeyExtensions.ParseOutputDescriptor(userStr, network)
            : null;
        var emulatorKey = contractData.TryGetValue("emulator", out var emulatorHex)
            ? ECXOnlyPubKey.Create(Convert.FromHexString(emulatorHex))
            : null;
        return new ArkProgramContract(server, program, args, user, emulatorKey);
    }

    private static string SerializeArgsMap(IReadOnlyDictionary<string, AsmToken> args)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in args)
        {
            obj[key] = value.Kind switch
            {
                AsmTokenKind.Bytes => "0x" + Convert.ToHexString(value.Bytes!).ToLowerInvariant(),
                AsmTokenKind.Number => value.Number!.Value.ToString(),
                _ => throw new InvalidOperationException($"Arg '{key}' must be bytes or a number."),
            };
        }
        return obj.ToJsonString();
    }

    private static Dictionary<string, AsmToken> DeserializeArgsMap(string json)
    {
        var obj = JsonNode.Parse(json)!.AsObject();
        var result = new Dictionary<string, AsmToken>();
        foreach (var (key, node) in obj)
        {
            var s = node!.GetValue<string>();
            result[key] = s.StartsWith("0x", StringComparison.Ordinal)
                ? AsmToken.FromBytes(Convert.FromHexString(s[2..]))
                : AsmToken.FromNumber(BigInteger.Parse(s));
        }
        return result;
    }
}

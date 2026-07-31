using System.Numerics;
using System.Text.Json.Nodes;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Program;
using NArk.Arkade.Program.Models;
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

    /// <inheritdoc />
    public override ContractScope DefaultScope => ContractScope.Offchain;

    private readonly ArkadeProgram _program;
    private readonly IReadOnlyDictionary<string, AsmToken> _args;
    private readonly OutputDescriptor? _user;
    private readonly ECXOnlyPubKey? _emulatorKey;
    private readonly IReadOnlyList<CompiledArkadeFunction> _compiled;

    /// <summary>Compiles an Arkade program into a spendable contract.</summary>
    /// <param name="server">Output descriptor for the Arkade server's key, bound as <c>$server</c>.</param>
    /// <param name="program">The parsed program artifact to compile.</param>
    /// <param name="args">Values for the program's declared params, by name.</param>
    /// <param name="user">
    /// Optional descriptor for the wallet's own key, bound as <c>$user</c> when the program declares it.
    /// </param>
    /// <param name="emulatorKey">
    /// Optional emulator signer key; required for functions carrying an ArkadeScript, since the
    /// emulator's per-script tweaked key becomes a required signer on that leaf.
    /// </param>
    public ArkProgramContract(
        OutputDescriptor server,
        ArkadeProgram program,
        IReadOnlyDictionary<string, AsmToken> args,
        OutputDescriptor? user = null,
        ECXOnlyPubKey? emulatorKey = null)
        : base(server)
    {
        _program = program;
        _user = user;
        _emulatorKey = emulatorKey;
        // Bind the conventional $server/$user params from this contract's own keys before
        // compiling, so a program can reference them without the caller wiring the pubkeys.
        _args = ApplyDefaultSignerArgs(program, args, server, user);

        var keys = new ArkadeProgramKeys
        {
            ServerKey = server.ToXOnlyPubKey(),
            UserKey = user?.ToXOnlyPubKey(),
            EmulatorKey = emulatorKey,
        };
        _compiled = ArkadeProgramCompiler.Compile(program, _args, keys);
    }

    /// <summary>
    /// Binds the conventional <c>server</c>/<c>user</c> parameters from the contract's own keys
    /// when the program declares them (in <see cref="ArkadeProgram.Params"/>) and the caller left
    /// them unbound — mirrors the ts-sdk's <c>Arkade.contract</c> defaulting. A program references
    /// these as ordinary <c>$server</c>/<c>$user</c> placeholders; explicit <paramref name="args"/>
    /// always win, and the resulting map is what gets persisted so a rebuilt contract re-derives
    /// the identical script.
    /// </summary>
    private static IReadOnlyDictionary<string, AsmToken> ApplyDefaultSignerArgs(
        ArkadeProgram program,
        IReadOnlyDictionary<string, AsmToken> args,
        OutputDescriptor server,
        OutputDescriptor? user)
    {
        var declared = program.Params;
        if (declared is null) return args;

        bool Declares(string name) => declared.Any(p => p.Name == name);
        
        Dictionary<string, AsmToken>? augmented = null;
        Dictionary<string, AsmToken> Ensure() => augmented ??= new Dictionary<string, AsmToken>(args);

        if (Declares("server") && !args.ContainsKey("server"))
        {
            Ensure()["server"] = AsmToken.FromBytes(server.ToXOnlyPubKey().ToBytes());
        }

        if (user is not null && Declares("user") && !args.ContainsKey("user"))
        {
            Ensure()["user"] = AsmToken.FromBytes(user.ToXOnlyPubKey().ToBytes());
        }

        return augmented ?? args;
    }

    /// <inheritdoc />
    public override string Type => ContractType;

    /// <summary>Output descriptor for the wallet's own key, bound as the <c>$user</c> param when the program declares it.</summary>
    public OutputDescriptor? User => _user;

    /// <summary>Args this program was compiled against.</summary>
    public IReadOnlyDictionary<string, AsmToken> Args => _args;

    /// <summary>All compiled spending paths, in declaration order.</summary>
    public IReadOnlyList<CompiledArkadeFunction> CompiledFunctions => _compiled;

    /// <summary>The compiled spending path with the given function name, if any.</summary>
    /// <param name="name">The function name as declared in the program artifact.</param>
    /// <returns>The compiled function, or <c>null</c> when the program declares no such function.</returns>
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
    /// <param name="contractData">The persisted key/value data produced by <c>GetContractData</c>.</param>
    /// <param name="network">The network to parse descriptors and keys against.</param>
    /// <returns>The rebuilt contract, compiled against the persisted program and args.</returns>
    public static ArkProgramContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var program = new ArkadeArtifactParser().ParseArtifact(JsonNode.Parse(contractData["program"])!.AsObject());
        var args = contractData.TryGetValue("args", out var argsJson)
            ? DeserializeArgsMap(argsJson, program)
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

    /// <summary>
    /// Decodes the persisted args map. A param with a declared <see cref="InputType"/> decodes by
    /// that type (no <c>0x</c>/decimal guessing); a bare param decodes by the <c>0x</c>-prefix
    /// heuristic. Mirrors the ts-sdk's <c>deserializeArkadeContractParams</c> / <c>parseTypedArgValue</c>.
    /// </summary>
    private static Dictionary<string, AsmToken> DeserializeArgsMap(string json, ArkadeProgram program)
    {
        var paramTypes = new Dictionary<string, InputType>();
        foreach (var p in program.Params ?? [])
        {
            if (p.Type is { } type) paramTypes[p.Name] = type;
        }

        var obj = JsonNode.Parse(json)!.AsObject();
        var result = new Dictionary<string, AsmToken>();
        foreach (var (key, node) in obj)
        {
            result[key] = paramTypes.TryGetValue(key, out var declaredType)
                ? ParseTypedArgValue(key, declaredType, node!)
                : ParseUntypedArgValue(node!);
        }
        return result;
    }

    private static AsmToken ParseUntypedArgValue(JsonNode node)
    {
        var s = node.GetValue<string>();
        return s.StartsWith("0x", StringComparison.Ordinal)
            ? AsmToken.FromBytes(Convert.FromHexString(s[2..]))
            : AsmToken.FromNumber(BigInteger.Parse(s));
    }

    private static AsmToken ParseTypedArgValue(string name, InputType type, JsonNode node)
    {
        if (type == InputType.Int)
        {
            return node.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? AsmToken.FromNumber(node.GetValue<long>())
                : AsmToken.FromNumber(BigInteger.Parse(node.GetValue<string>()));
        }

        var s = node.GetValue<string>();
        if (s.StartsWith("0x", StringComparison.Ordinal))
        {
            return AsmToken.FromBytes(Convert.FromHexString(s[2..]));
        }

        throw new InvalidOperationException(
            $"arkade contract params: '{name}' expects {type.ToString().ToLowerInvariant()} as 0x-prefixed hex, got '{s}'.");
    }
}

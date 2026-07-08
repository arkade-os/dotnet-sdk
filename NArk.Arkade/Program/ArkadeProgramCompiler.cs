using System.Numerics;
using NArk.Arkade.Crypto;
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Program;

/// <summary>
/// Compiles an <see cref="ArkadeProgram"/> (plus constructor <c>args</c> and signer
/// <see cref="ArkadeProgramKeys"/>) into per-function leaf scripts. Mirrors the ts-sdk's
/// <c>compileFunctions</c>/<c>resolveAsm</c>/<c>resolveSigner</c>/<c>validateTapscript</c>,
/// but reuses this codebase's existing tapleaf conventions (<see cref="NofNMultisigTapScript"/>,
/// the "last <c>OP_CHECKSIGVERIFY</c> becomes <c>OP_CHECKSIG</c>" trick from
/// <see cref="UnilateralPathArkTapScript"/>) instead of porting ts-sdk's parallel
/// <c>*Tapscript</c> builders.
/// </summary>
public static class ArkadeProgramCompiler
{
    /// <summary>
    /// Compile every function in <paramref name="program"/> against <paramref name="args"/>
    /// and <paramref name="keys"/>.
    /// </summary>
    /// <param name="args">
    /// Bound values for the program's <c>$param</c> placeholders. Only
    /// <see cref="AsmTokenKind.Bytes"/> and <see cref="AsmTokenKind.Number"/> tokens
    /// are meaningful values here — mirrors the ts-sdk's <c>ArkadeParamValue</c>
    /// (<c>Uint8Array | bigint | number</c>, no string).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The program's version is unsupported, it has no functions, a tapscript segment
    /// fails validation, a <c>$param</c>/signer reference cannot be resolved, or a
    /// covenant segment is present without an emulator key configured.
    /// </exception>
    public static IReadOnlyList<CompiledArkadeFunction> Compile(
        ArkadeProgram program,
        IReadOnlyDictionary<string, AsmToken> args,
        ArkadeProgramKeys keys)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(keys);

        if (program.Version != ArkadeProgram.SupportedVersion)
        {
            throw new InvalidOperationException(
                $"ArkadeProgram version {program.Version} is not supported (this SDK supports version {ArkadeProgram.SupportedVersion}).");
        }

        if (program.Functions.Count == 0)
        {
            throw new InvalidOperationException("ArkadeProgram has no functions.");
        }

        return program.Functions
            .Select(kv => CompileFunction(kv.Key, kv.Value, args, keys))
            .ToList();
    }

    private static CompiledArkadeFunction CompileFunction(
        string name,
        ArkadeFunction def,
        IReadOnlyDictionary<string, AsmToken> args,
        ArkadeProgramKeys keys)
    {
        ValidateTapscript(def.Tapscript);

        var pubkeys = def.Tapscript.Signers
            .Select(signer => ResolveSigner(signer, keys, args))
            .ToList();

        byte[]? arkadeScriptBytes = null;
        TaprootPubKey? emulatorKey = null;
        if (def.ScriptSegment is { } covenant)
        {
            emulatorKey = new TaprootPubKey((keys.EmulatorKey
                ?? throw new InvalidOperationException(
                    $"Function '{name}' has a covenant segment but no emulator key was configured.")).ToBytes());

            arkadeScriptBytes = ArkadeScript.Encode(ResolveAsmOps(covenant.Asm, args));
            var tweaked = ArkadeTweak.Tweak(emulatorKey, arkadeScriptBytes);
            pubkeys.Add(ECXOnlyPubKey.Create(tweaked.ToBytes()));
        }

        var leafScript = ArkadeScript.Encode(EncodeTapscriptSegment(def.Tapscript, pubkeys, args));

        return new CompiledArkadeFunction
        {
            Name = name,
            Definition = def,
            LeafScript = leafScript,
            ArkadeScriptBytes = arkadeScriptBytes,
            EmulatorKey = emulatorKey,
        };
    }

    /// <summary>
    /// Validates that a segment has at least one signer, at most one of
    /// <see cref="TapscriptSegment.Asm"/>/<see cref="TapscriptSegment.Csv"/>/
    /// <see cref="TapscriptSegment.Cltv"/>, and no Arkade extension opcodes in
    /// <see cref="TapscriptSegment.Asm"/> (those are <c>OP_SUCCESS</c> on-chain and
    /// belong in the covenant segment instead).
    /// </summary>
    private static void ValidateTapscript(TapscriptSegment seg)
    {
        if (seg.Signers.Count == 0)
        {
            throw new InvalidOperationException("tapscript: at least one signer is required.");
        }

        var formCount = new[] { seg.Asm is not null, seg.Csv is not null, seg.Cltv is not null }.Count(x => x);
        if (formCount > 1)
        {
            throw new InvalidOperationException("tapscript: 'asm', 'csv' and 'cltv' conflict — use at most one.");
        }

        if (seg.Asm is null) return;

        foreach (var token in seg.Asm)
        {
            if (token.Kind != AsmTokenKind.Text || token.IsParam) continue;
            if (ArkadeOpcodeRegistry.GetOpcodeValue(token.Text!) is { } value && ArkadeOpcodeRegistry.IsArkadeOpcode(value))
            {
                throw new InvalidOperationException(
                    $"tapscript: arkade opcode '{token.Text}' is not enforceable on-chain — move it to the covenant segment.");
            }
        }
    }

    /// <summary>
    /// Builds the leaf ops for a tapscript segment: an N-of-N over <paramref name="pubkeys"/>
    /// (via <see cref="NofNMultisigTapScript"/>, with its final <c>OP_CHECKSIGVERIFY</c> swapped
    /// to a plain <c>OP_CHECKSIG</c> — the same convention <see cref="UnilateralPathArkTapScript"/>
    /// uses), optionally gated by a CSV/CLTV timelock or a resolved condition script.
    /// </summary>
    private static IEnumerable<Op> EncodeTapscriptSegment(
        TapscriptSegment seg,
        IReadOnlyList<ECXOnlyPubKey> pubkeys,
        IReadOnlyDictionary<string, AsmToken> args)
    {
        var multisigOps = new NofNMultisigTapScript(pubkeys.ToArray()).BuildScript().ToList();
        multisigOps[^1] = OpcodeType.OP_CHECKSIG;

        if (seg.Csv is { } csv)
        {
            return [Op.GetPushOp(csv.Value), OpcodeType.OP_CHECKSEQUENCEVERIFY, OpcodeType.OP_DROP, .. multisigOps];
        }

        if (seg.Cltv is { } cltv)
        {
            return [Op.GetPushOp(cltv.Value), OpcodeType.OP_CHECKLOCKTIMEVERIFY, OpcodeType.OP_DROP, .. multisigOps];
        }

        if (seg.Asm is { } asm)
        {
            var conditionOps = ResolveAsmOps(asm, args).ToList();
            conditionOps.Add(OpcodeType.OP_VERIFY);
            return [.. conditionOps, .. multisigOps];
        }

        return multisigOps;
    }

    private static ECXOnlyPubKey ResolveSigner(
        AsmToken signer,
        ArkadeProgramKeys keys,
        IReadOnlyDictionary<string, AsmToken> args)
    {
        var resolved = ResolveToken(signer, args);

        if (resolved.Kind == AsmTokenKind.Bytes)
        {
            return ECXOnlyPubKey.Create(resolved.Bytes!);
        }

        if (resolved.Kind != AsmTokenKind.Text)
        {
            throw new InvalidOperationException("A signer token must resolve to text (\"server\"/\"user\") or bytes (a pubkey).");
        }

        return resolved.Text switch
        {
            "server" => keys.ServerKey,
            "user" => keys.UserKey ?? throw new InvalidOperationException("Signer 'user' requires a configured user key."),
            _ => throw new InvalidOperationException($"Unknown signer reference '{resolved.Text}'."),
        };
    }

    /// <summary>Substitutes a <c>$param</c> token with its bound value; passes any other token through unchanged.</summary>
    private static AsmToken ResolveToken(AsmToken token, IReadOnlyDictionary<string, AsmToken> args)
    {
        if (!token.IsParam) return token;

        return args.TryGetValue(token.ParamName, out var value)
            ? value
            : throw new InvalidOperationException($"Unbound parameter '{token.ParamName}'.");
    }

    private static IEnumerable<Op> ResolveAsmOps(IEnumerable<AsmToken> asm, IReadOnlyDictionary<string, AsmToken> args)
        => asm.Select(t => TokenToOp(ResolveToken(t, args)));

    private static Op TokenToOp(AsmToken token) => token.Kind switch
    {
        AsmTokenKind.Bytes => Op.GetPushOp(token.Bytes!),
        AsmTokenKind.Number => NumberToOp(token.Number!.Value),
        AsmTokenKind.Text => OpcodeNameToOp(token.Text!),
        _ => throw new InvalidOperationException("Unknown token kind."),
    };

    private static Op OpcodeNameToOp(string name)
    {
        var value = ArkadeOpcodeRegistry.GetOpcodeValue(name)
                    ?? throw new InvalidOperationException($"Unknown opcode '{name}'.");
        return (OpcodeType)value;
    }

    /// <summary>
    /// Bitcoin's minimal-push convention (0/±1..16 as single-byte opcodes, else a data
    /// push) applies regardless of magnitude — <see cref="Op.GetPushOp(long)"/> already
    /// implements it for values that fit in a <see cref="long"/>; wider EC-scalar-sized
    /// values fall back to a raw <see cref="ArkadeScriptNum"/> push.
    /// </summary>
    internal static Op NumberToOp(BigInteger value)
        => value >= long.MinValue && value <= long.MaxValue
            ? Op.GetPushOp((long)value)
            : Op.GetPushOp(ArkadeScriptNum.Encode(value));
}

using NArk.Arkade.Program.Models;

namespace NArk.Arkade.Program;

/// <summary>
/// Validates an <see cref="ArkadeProgram"/>'s constructor-parameter declarations against the bound
/// args. When a program declares <see cref="ArkadeProgram.Params"/> they are authoritative: every
/// declared param must be bound, every <c>$name</c> reference in the program must be declared, and
/// every typed entry (<see cref="TypedInput.Type"/> set) validates its bound value. Bare
/// (untyped) entries still get the structural checks; only the value type-check is skipped.
/// </summary>
/// <remarks>
/// .NET models timelocks as concrete <see cref="NBitcoin.Sequence"/>/<see cref="NBitcoin.LockTime"/>
/// rather than <c>$param</c> references, so — unlike the ts-sdk — <c>csv</c>/<c>cltv</c> carry no
/// param references and are not walked here.
/// </remarks>
public static class ArkadeProgramValidator
{
    /// <summary>Byte lengths enforced for typed values (x-only pubkey, BIP-340 signature).</summary>
    private static readonly IReadOnlyDictionary<InputType, int> TypedByteLengths =
        new Dictionary<InputType, int> { [InputType.Pubkey] = 32, [InputType.Sig] = 64 };

    /// <summary>
    /// Validate <paramref name="program"/>'s params against <paramref name="args"/>. A no-op only
    /// when the program declares no params.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A declared param is unbound, a <c>$name</c> reference is undeclared, or a typed value has the
    /// wrong kind/length.
    /// </exception>
    public static void Validate(ArkadeProgram program, IReadOnlyDictionary<string, AsmToken> args)
    {
        var declaredParams = program.Params;
        if (declaredParams is null) return;

        var declared = new HashSet<string>(declaredParams.Select(p => p.Name));

        foreach (var name in declared)
        {
            if (!args.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"program parameter '{name}' is declared but not bound in args.");
            }
        }

        foreach (var reference in CollectParamRefs(program))
        {
            if (!declared.Contains(reference))
            {
                throw new InvalidOperationException(
                    $"'${reference}' is referenced but not declared in program params.");
            }
        }

        foreach (var p in declaredParams)
        {
            if (p.Type is { } type) ValidateParamValue(p.Name, type, args[p.Name]);
        }
    }

    private static void ValidateParamValue(string name, InputType type, AsmToken value)
    {
        if (type == InputType.Int)
        {
            if (value.Kind != AsmTokenKind.Number)
            {
                throw new InvalidOperationException(
                    $"program parameter '{name}' expects an int, got bytes.");
            }
            return;
        }

        if (value.Kind != AsmTokenKind.Bytes)
        {
            throw new InvalidOperationException(
                $"program parameter '{name}' expects {TypeName(type)} bytes, got a number.");
        }

        if (TypedByteLengths.TryGetValue(type, out var length) && value.Bytes!.Length != length)
        {
            throw new InvalidOperationException(
                $"program parameter '{name}' expects a {length}-byte {TypeName(type)}, got {value.Bytes!.Length} bytes.");
        }
    }

    /// <summary>Collect every <c>$name</c> reference across the program's signers, asm and witness lists.</summary>
    private static IEnumerable<string> CollectParamRefs(ArkadeProgram program)
    {
        var refs = new HashSet<string>();

        void Collect(IEnumerable<AsmToken>? tokens)
        {
            foreach (var token in tokens ?? [])
            {
                if (token.IsParam) refs.Add(token.ParamName);
            }
        }

        foreach (var fn in program.Functions.Values)
        {
            Collect(fn.Tapscript.Signers);
            Collect(fn.Tapscript.Asm);
            Collect(fn.Tapscript.Witness);
            Collect(fn.ScriptSegment?.Asm);
            Collect(fn.ScriptSegment?.Witness);
        }

        return refs;
    }

    private static string TypeName(InputType type) => type switch
    {
        InputType.Bytes => "bytes",
        InputType.Pubkey => "pubkey",
        InputType.Sig => "sig",
        InputType.Hash => "hash",
        InputType.Int => "int",
        _ => type.ToString().ToLowerInvariant(),
    };
}

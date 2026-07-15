namespace NArk.Arkade.Program;

/// <summary>
/// An Arkade contract program (hand-written or compiler-emitted): named functions,
/// each split into a <see ArkadeFunctionkadeFn.Tapscript"/> segment enforced on-chain
/// and an optional <see ArkadeFunctionkadeFn.ScriptSegment"/> emulated by the
/// co-signing service. Mirrors the ts-sdk's <c>Program</c>.
/// </summary>
/// <remarks>
/// This type is a pure data model: it does not validate signers/timelock exclusivity,
/// resolve <c>$param</c> placeholders, or compile anything to script bytes. Those are
/// later, separate steps.
/// </remarks>
public sealed class ArkadeProgram
{
    /// <summary>The only program version this SDK understands.</summary>
    public const int SupportedVersion = 0;

    /// <summary>The artifact format version.</summary>
    public required int Version { get; init; }

    /// <summary>
    /// Ordered constructor parameters. When declared, they are authoritative:
    /// <see cref="ArkadeProgramValidator"/> requires every param to be bound and every <c>$name</c>
    /// reference in the program to be declared here. Entries may be typed descriptors
    /// (<see cref="TypedInput.Type"/> set — the bound value is type-validated) or bare names (via
    /// the implicit <see cref="TypedInput"/> conversion, e.g. <c>["server", "user"]</c> — checked
    /// structurally but not type-validated). Mirrors the ts-sdk's <c>params: InputRef[]</c>.
    /// </summary>
    public IReadOnlyList<TypedInput>? Params { get; init; }

    /// <summary>The program's named spending paths, keyed by function name.</summary>
    public required IReadOnlyDictionary<string, ArkadeFunction> Functions { get; init; }
}

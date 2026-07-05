namespace NArk.Arkade.Program;

/// <summary>
/// An Arkade contract program (hand-written or compiler-emitted): named functions,
/// each split into a <see cref="ArkadeFunction.Tapscript"/> segment enforced on-chain
/// and an optional <see cref="ArkadeFunction.CovenantSegment"/> emulated by the
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

    /// <summary>Ordered constructor parameter names (documentation/validation only).</summary>
    public IReadOnlyList<string>? Params { get; init; }

    /// <summary>The program's named spending paths, keyed by function name.</summary>
    public required IReadOnlyDictionary<string, ArkadeFunction> Functions { get; init; }
}

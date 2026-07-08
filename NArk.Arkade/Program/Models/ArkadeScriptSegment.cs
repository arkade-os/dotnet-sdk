namespace NArk.Arkade.Program;

/// <summary>
/// The ArkadeScript segment of a spending path — emulated by the co-signing service,
/// bound to the tapscript leaf via the key tweak. Mirrors the ts-sdk's <c>ArkadeSegment</c>
/// </summary>
public sealed class ArkadeScriptSegment
{
    /// <summary>Raw Arkade opcodes with <c>$param</c> placeholders.</summary>
    public required IReadOnlyList<AsmToken> Asm { get; init; }

    /// <summary>The arkade-script witness stack (e.g. an output index).</summary>
    public IReadOnlyList<AsmToken>? Witness { get; init; }
}

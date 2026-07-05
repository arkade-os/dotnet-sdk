namespace NArk.Arkade.Program;

/// <summary>
/// The ArkadeScript segment of a spending path — emulated by the co-signing service,
/// bound to the tapscript leaf via the key tweak. Mirrors the ts-sdk's <c>ArkadeSegment</c>
/// (named "Covenant" here, not "Arkade", to avoid clashing with <see cref="Scripts.ArkadeScript"/>).
/// </summary>
public sealed class ArkadeCovenantSegment
{
    /// <summary>Raw Arkade opcodes with <c>$param</c> placeholders.</summary>
    public required IReadOnlyList<ArkadeToken> Asm { get; init; }

    /// <summary>The arkade-script witness stack (e.g. an output index).</summary>
    public IReadOnlyList<ArkadeToken>? Witness { get; init; }
}

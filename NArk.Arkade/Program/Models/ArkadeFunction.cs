namespace NArk.Arkade.Program.Models;

/// <summary>
/// One named spending path of an <see cref="ArkadeProgram"/>. Mirrors the ts-sdk's
/// <c>ArkadeFunction</c>.
/// </summary>
public sealed class ArkadeFunction
{
    /// <summary>
    /// The function's call arguments, in order. Absent for nullary paths (e.g. exit/cancel).
    /// </summary>
    public IReadOnlyList<TypedInput>? Inputs { get; init; }

    /// <summary>The on-chain-enforced segment of this spending path.</summary>
    public required TapscriptSegment Tapscript { get; init; }

    /// <summary>The emulator-executed segment, present only for covenant paths.</summary>
    public ArkadeScriptSegment? ScriptSegment { get; init; }
}

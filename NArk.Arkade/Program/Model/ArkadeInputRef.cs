namespace NArk.Arkade.Program;

/// <summary>
/// A function input declaration: a name, plus an optional declared <see cref="ArkadeArgType"/>.
/// Mirrors the ts-sdk's <c>InputRef</c> (<c>string | { name, type }</c>) — a bare name
/// (<see cref="Type"/> is <c>null</c>) falls back to the loose <c>ArkadeArgValue</c> at
/// call sites; a typed descriptor gives the call argument a precise type.
/// </summary>
public sealed class ArkadeInputRef
{
    /// <summary>The input's name — how it is referenced from <c>witness</c> lists.</summary>
    public required string Name { get; init; }

    /// <summary>The input's declared type, or <c>null</c> for a bare (untyped) input.</summary>
    public ArkadeArgType? Type { get; init; }
}

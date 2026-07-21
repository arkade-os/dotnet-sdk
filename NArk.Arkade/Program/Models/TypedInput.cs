namespace NArk.Arkade.Program.Models;

/// <summary>
/// The declared type of a function input. Mirrors the ts-sdk's <c>ArkadeArgType</c>
/// (<c>"bytes" | "pubkey" | "sig" | "hash" | "int"</c>) — used only for documentation /
/// call-site typing, not for validation.
/// </summary>
public enum InputType
{
    /// <summary>Arbitrary byte string.</summary>
    Bytes,

    /// <summary>A public key.</summary>
    Pubkey,

    /// <summary>A signature.</summary>
    Sig,

    /// <summary>A hash (e.g. an HTLC preimage's image).</summary>
    Hash,

    /// <summary>An integer (script-num at resolve time).</summary>
    Int,
}


/// <summary>
/// A function input declaration: a name, plus an optional declared <see cref="ArkadeProgramInputType"/>.
/// Mirrors the ts-sdk's <c>InputRef</c> (<c>string | { name, type }</c>) — a bare name
/// (<see cref="Type"/> is <c>null</c>) falls back to the loose <c>ArkadeArgValue</c> at
/// call sites; a typed descriptor gives the call argument a precise type.
/// </summary>
public sealed class TypedInput
{
    /// <summary>The input's name — how it is referenced from <c>witness</c> lists.</summary>
    public required string Name { get; init; }

    /// <summary>The input's declared type, or <c>null</c> for a bare (untyped) input.</summary>
    public InputType? Type { get; init; }

    /// <summary>
    /// Implicitly wrap a bare (untyped) input/param name — mirrors the ts-sdk's bare-string
    /// <c>InputRef</c>. Lets callers write <c>Params = ["server", "hash"]</c> alongside typed
    /// descriptors (<c>new FunctionInput { Name = "hash", Type = InputType.Hash }</c>).
    /// </summary>
    public static implicit operator TypedInput(string name) => new() { Name = name };
}


namespace NArk.Arkade.Program;

/// <summary>
/// The declared type of a function input. Mirrors the ts-sdk's <c>ArkadeArgType</c>
/// (<c>"bytes" | "pubkey" | "sig" | "hash" | "int"</c>) — used only for documentation /
/// call-site typing, not for validation.
/// </summary>
public enum ArkadeArgType
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

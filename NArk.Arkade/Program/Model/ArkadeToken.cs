using System.Numerics;

namespace NArk.Arkade.Program;

/// <summary>The kind of value an <see cref="ArkadeToken"/> carries.</summary>
public enum ArkadeTokenKind
{
    /// <summary>An opcode mnemonic, a <c>$param</c> placeholder, or a signer keyword (<c>"server"</c>/<c>"user"</c>).</summary>
    Text,

    /// <summary>A numeric literal (script-num at resolve time).</summary>
    Number,

    /// <summary>Raw bytes (a data push, or a literal pubkey/hash).</summary>
    Bytes,
}

/// <summary>
/// One entry in an <see cref="ArkadeTapscriptSegment"/>/<see cref="ArkadeCovenantSegment"/>'s
/// <c>Asm</c>/<c>Witness</c>/<c>Signers</c> list — mirrors the ts-sdk's <c>AsmToken</c>/
/// <c>WitnessRef</c>/<c>SignerRef</c> unions (<c>string | number | bigint | Uint8Array</c>).
/// </summary>
/// <remarks>
/// This is a pure data holder: it does not resolve <c>$param</c> placeholders, look up
/// opcodes, or encode anything. Resolution happens later, once constructor <c>args</c>
/// (and, for witness tokens, call args) are known.
/// </remarks>
public readonly struct ArkadeToken
{
    /// <summary>Which of <see cref="Text"/>/<see cref="Number"/>/<see cref="Bytes"/> is set.</summary>
    public ArkadeTokenKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="ArkadeTokenKind.Text"/>.</summary>
    public string? Text { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="ArkadeTokenKind.Number"/>.</summary>
    public BigInteger? Number { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="ArkadeTokenKind.Bytes"/>.</summary>
    public byte[]? Bytes { get; }

    private ArkadeToken(ArkadeTokenKind kind, string? text, BigInteger? number, byte[]? bytes)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Bytes = bytes;
    }

    /// <summary>Wrap an opcode mnemonic, <c>$param</c> placeholder, or signer keyword.</summary>
    public static ArkadeToken FromText(string text) => new(ArkadeTokenKind.Text, text, null, null);

    /// <summary>Wrap a numeric literal.</summary>
    public static ArkadeToken FromNumber(BigInteger number) => new(ArkadeTokenKind.Number, null, number, null);

    /// <summary>Wrap raw bytes.</summary>
    public static ArkadeToken FromBytes(byte[] bytes) => new(ArkadeTokenKind.Bytes, null, null, bytes);

    /// <summary>True when this is a <c>$param</c> placeholder (a <see cref="Text"/> token starting with <c>$</c>).</summary>
    public bool IsParam => Kind == ArkadeTokenKind.Text && Text is { Length: > 0 } t && t[0] == '$';

    /// <summary>The parameter name for a <see cref="IsParam"/> token (without the <c>$</c> prefix).</summary>
    public string ParamName => IsParam ? Text![1..] : throw new InvalidOperationException("Token is not a $param placeholder.");
}

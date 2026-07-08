using System.Numerics;

namespace NArk.Arkade.Program;

/// <summary>The kind of value an <see cref="AsmToken"/> carries.</summary>
public enum AsmTokenKind
{
    /// <summary>An opcode mnemonic, a <c>$param</c> placeholder, or a signer keyword (<c>"server"</c>/<c>"user"</c>).</summary>
    Text,

    /// <summary>A numeric literal (script-num at resolve time).</summary>
    Number,

    /// <summary>Raw bytes (a data push, or a literal pubkey/hash).</summary>
    Bytes,
}

/// <summary>
/// One entry in an <see cref="TapscriptSegment"/>/<see cref="ArkadeScriptSegment"/>'s
/// <c>Asm</c>/<c>Witness</c>/<c>Signers</c> list — mirrors the ts-sdk's <c>AsmToken</c>/
/// <c>WitnessRef</c>/<c>SignerRef</c> unions (<c>string | number | bigint | Uint8Array</c>).
/// </summary>
/// <remarks>
/// A pure data holder with value semantics: it does not resolve <c>$param</c> placeholders,
/// look up opcodes, or encode anything. Resolution happens later, once constructor <c>args</c>
/// (and, for witness tokens, call args) are known. The implicit conversions let callers write
/// asm/witness lists as bare literals (<c>["HASH160", "$hash", 42]</c>) instead of factory calls.
/// </remarks>
public readonly struct AsmToken : IEquatable<AsmToken>
{
    /// <summary>Which of <see cref="Text"/>/<see cref="Number"/>/<see cref="Bytes"/> is set.</summary>
    public AsmTokenKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="AsmTokenKind.Text"/>.</summary>
    public string? Text { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="AsmTokenKind.Number"/>.</summary>
    public BigInteger? Number { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="AsmTokenKind.Bytes"/>.</summary>
    public byte[]? Bytes { get; }

    private AsmToken(AsmTokenKind kind, string? text, BigInteger? number, byte[]? bytes)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Bytes = bytes;
    }

    /// <summary>Wrap an opcode mnemonic, <c>$param</c> placeholder, or signer keyword.</summary>
    public static AsmToken FromText(string text) => new(AsmTokenKind.Text, text, null, null);

    /// <summary>Wrap a numeric literal.</summary>
    public static AsmToken FromNumber(BigInteger number) => new(AsmTokenKind.Number, null, number, null);

    /// <summary>Wrap raw bytes.</summary>
    public static AsmToken FromBytes(byte[] bytes) => new(AsmTokenKind.Bytes, null, null, bytes);

    /// <summary>Implicitly wrap an opcode mnemonic / <c>$param</c> / signer keyword — see <see cref="FromText"/>.</summary>
    public static implicit operator AsmToken(string text) => FromText(text);

    /// <summary>Implicitly wrap an integer literal (also covers <c>int</c> via widening) — see <see cref="FromNumber"/>.</summary>
    public static implicit operator AsmToken(long number) => FromNumber(number);

    /// <summary>Implicitly wrap an arbitrary-precision integer — see <see cref="FromNumber"/>.</summary>
    public static implicit operator AsmToken(BigInteger number) => FromNumber(number);

    /// <summary>Implicitly wrap raw bytes — see <see cref="FromBytes"/>.</summary>
    public static implicit operator AsmToken(byte[] bytes) => FromBytes(bytes);

    /// <summary>True when this is a <c>$param</c> placeholder (a <see cref="Text"/> token starting with <c>$</c>).</summary>
    public bool IsParam => Kind == AsmTokenKind.Text && Text is { Length: > 0 } t && t[0] == '$';

    /// <summary>The parameter name for a <see cref="IsParam"/> token (without the <c>$</c> prefix).</summary>
    public string ParamName => IsParam ? Text![1..] : throw new InvalidOperationException("Token is not a $param placeholder.");

    /// <summary>
    /// Value equality: same <see cref="Kind"/> and payload. Text compares ordinally (opcode/param/
    /// signer names are case-sensitive, matching how they resolve), bytes compare by content.
    /// </summary>
    public bool Equals(AsmToken other) =>
        Kind == other.Kind
        && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && Nullable.Equals(Number, other.Number)
        && BytesEqual(Bytes, other.Bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AsmToken other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((int)Kind);
        hash.Add(Text, StringComparer.Ordinal);
        hash.Add(Number);
        if (Bytes is not null) hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    /// <summary>Value equality — see <see cref="Equals(AsmToken)"/>.</summary>
    public static bool operator ==(AsmToken left, AsmToken right) => left.Equals(right);

    /// <summary>Value inequality — see <see cref="Equals(AsmToken)"/>.</summary>
    public static bool operator !=(AsmToken left, AsmToken right) => !left.Equals(right);

    private static bool BytesEqual(byte[]? a, byte[]? b) => (a, b) switch
    {
        (null, null) => true,
        (not null, not null) => a.AsSpan().SequenceEqual(b),
        _ => false,
    };
}

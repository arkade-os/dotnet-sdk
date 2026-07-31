using System.Globalization;
using System.Text;
using NBitcoin;

namespace NArk.Arkade.Scripts;

/// <summary>
/// Encode/decode + ASM helpers for ArkadeScript — the Bitcoin-Script superset
/// the emulator executes. Mirrors the ts-sdk's <c>ArkadeScript</c> coder
/// (and its <c>toASM</c> / <c>fromASM</c> helpers) so that the same script
/// bytes round-trip across the .NET, TypeScript, and Go SDKs.
/// </summary>
/// <remarks>
/// <para>
/// Encoding is a thin pass-through over NBitcoin's <see cref="Script"/> /
/// <see cref="Op"/> primitives — NBitcoin already treats any byte outside the
/// push range (<c>0x01–0x4e</c>) as an opaque single-byte opcode, so all 41
/// Arkade extension opcodes survive the trip without a custom serializer.
/// </para>
/// <para>
/// ASM formatting and parsing route through <see cref="ArkadeOpcodeRegistry"/>
/// so Arkade extension opcodes get their canonical <c>OP_INSPECTOUTPUTVALUE</c>-
/// style names instead of NBitcoin's <c>OP_UNKNOWN</c> placeholder.
/// </para>
/// </remarks>
public static class ArkadeScript
{
    /// <summary>
    /// Encode an op stream into the canonical ArkadeScript byte representation.
    /// Equivalent to the ts-sdk's <c>ArkadeScript.encode(...)</c>.
    /// </summary>
    public static byte[] Encode(IEnumerable<Op> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var script = new Script(ops);
        return script.ToBytes();
    }

    /// <summary>
    /// Decode an ArkadeScript byte stream into an op list. Equivalent to the
    /// ts-sdk's <c>ArkadeScript.decode(...)</c>. Bytes outside the data-push
    /// range are surfaced as <see cref="Op"/>s with <see cref="Op.Code"/>
    /// holding the raw byte; use <see cref="ArkadeOpcodeRegistry.GetOpcodeName"/>
    /// to resolve the mnemonic.
    /// </summary>
    public static IReadOnlyList<Op> Decode(byte[] script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return new Script(script).ToOps().ToList();
    }

    /// <summary>
    /// Format an op stream as Bitcoin-style ASM (e.g.
    /// <c>"OP_DUP OP_HASH160 deadbeef OP_INSPECTOUTPUTVALUE"</c>). Data pushes
    /// are lowercase hex; <see cref="OpcodeType.OP_0"/> through
    /// <see cref="OpcodeType.OP_16"/> render as their <c>OP_N</c> mnemonics
    /// to match the ts-sdk's output.
    /// </summary>
    public static string ToAsm(IEnumerable<Op> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var sb = new StringBuilder();
        foreach (var op in ops)
        {
            if (sb.Length > 0) sb.Append(' ');

            var opcode = (byte)op.Code;

            // Small-integer opcodes must be handled before the push branch. NBitcoin
            // exposes them with PushData set (OP_0 carries an empty array, OP_16 carries
            // [0x10]), so rendering them as hex would emit "" for OP_0 — which FromAsm's
            // whitespace split then drops — and "10" for OP_16, which FromAsm resolves
            // back to OP_10. Emitting the mnemonic is also what this method documents.
            if (opcode == (byte)OpcodeType.OP_0 ||
                (opcode >= (byte)OpcodeType.OP_1 && opcode <= (byte)OpcodeType.OP_16))
            {
                sb.Append(ArkadeOpcodeRegistry.GetOpcodeName(opcode) ?? $"OP_UNKNOWN_{opcode:x2}");
                continue;
            }

            if (op.PushData is { } data)
            {
                // Data push — render as hex, mirroring @scure/base hex.encode (lowercase).
                sb.Append(Convert.ToHexString(data).ToLowerInvariant());
                continue;
            }

            sb.Append(ArkadeOpcodeRegistry.GetOpcodeName(opcode) ?? $"OP_UNKNOWN_{opcode:x2}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a Bitcoin-style ASM string back into an op list — the inverse of
    /// <see cref="ToAsm"/>. Tokens are: <c>OP_*</c> opcode mnemonics
    /// (with or without the prefix), <c>OP_0..OP_16</c> small-integer
    /// shortcuts, or even-length hex strings interpreted as data pushes.
    /// Small integers must carry the <c>OP_</c> prefix: a bare <c>"11"</c> is a
    /// one-byte data push, not <c>OP_11</c>, since the two are otherwise
    /// indistinguishable in the hex rendering <see cref="ToAsm"/> emits.
    /// </summary>
    /// <exception cref="FormatException">A token cannot be resolved as either an opcode or a hex push.</exception>
    public static IReadOnlyList<Op> FromAsm(string asm)
    {
        ArgumentNullException.ThrowIfNull(asm);
        var tokens = asm.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var ops = new List<Op>(tokens.Length);

        foreach (var token in tokens)
        {
            // Small-integer shortcuts: OP_0 / OP_FALSE / OP_TRUE / OP_1..OP_16
            // get the same single-byte representation NBitcoin emits via
            // Op.GetPushOp(long).
            if (token is "OP_0" or "OP_FALSE")
            {
                ops.Add(OpcodeType.OP_0);
                continue;
            }
            if (token is "OP_TRUE" or "OP_1")
            {
                ops.Add(OpcodeType.OP_1);
                continue;
            }
            if (TryParseOpN(token, out var n))
            {
                ops.Add(Op.GetPushOp(n));
                continue;
            }

            var opcodeValue = ArkadeOpcodeRegistry.GetOpcodeValue(token);
            if (opcodeValue is { } b)
            {
                // NBitcoin has an implicit OpcodeType→Op conversion; the cast
                // also accepts arbitrary byte values (e.g. Arkade extension
                // opcodes that aren't named in the OpcodeType enum), so the
                // round-trip stays byte-exact.
                ops.Add((OpcodeType)b);
                continue;
            }

            // Last-resort: even-length lowercase/uppercase hex → data push.
            if (TryParseHex(token, out var pushed))
            {
                ops.Add(Op.GetPushOp(pushed));
                continue;
            }

            throw new FormatException($"Invalid ASM token: '{token}'");
        }

        return ops;
    }

    /// <summary>Encode an ASM string straight to bytes — convenience over <see cref="FromAsm"/> + <see cref="Encode"/>.</summary>
    public static byte[] AsmToBytes(string asm) => Encode(FromAsm(asm));

    /// <summary>Decode bytes straight to ASM — convenience over <see cref="Decode"/> + <see cref="ToAsm"/>.</summary>
    public static string BytesToAsm(byte[] script) => ToAsm(Decode(script));

    private static bool TryParseOpN(string token, out int n)
    {
        n = 0;
        if (!token.StartsWith("OP_", StringComparison.Ordinal)) return false;
        var rest = token.AsSpan(3);
        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return false;
        return n is >= 1 and <= 16;
    }

    private static bool TryParseHex(string token, out byte[] bytes)
    {
        bytes = [];
        if (token.Length == 0 || (token.Length & 1) != 0) return false;
        try
        {
            bytes = Convert.FromHexString(token);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

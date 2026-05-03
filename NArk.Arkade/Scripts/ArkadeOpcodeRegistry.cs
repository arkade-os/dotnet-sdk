using System.Collections.Frozen;

namespace NArk.Arkade.Scripts;

/// <summary>
/// Combined opcode-name registry covering both standard Bitcoin opcodes (via
/// NBitcoin's <see cref="NBitcoin.Op"/>) and the Arkade extension opcodes from
/// <see cref="ArkadeOpcode"/>. Mirrors the ts-sdk's <c>OPCODE_NAMES</c> /
/// <c>OPCODE_VALUES</c> tables so ASM round-trips agree across SDKs.
/// </summary>
public static class ArkadeOpcodeRegistry
{
    private static readonly FrozenDictionary<byte, string> NameByValue;
    private static readonly FrozenDictionary<string, byte> ValueByName;

    static ArkadeOpcodeRegistry()
    {
        // Arkade extensions first — we know the values are unique across the
        // <c>0xb3</c> + <c>0xc4–0xf3</c> range and don't collide with anything
        // standard (the 0xb3 slot is the repurposed NOP4).
        var nameByValue = new Dictionary<byte, string>();
        var valueByName = new Dictionary<string, byte>(StringComparer.Ordinal);

        foreach (var opcode in Enum.GetValues<ArkadeOpcode>())
        {
            var name = opcode.ToString();              // e.g. "OP_MERKLEBRANCHVERIFY"
            var bareName = name.StartsWith("OP_") ? name[3..] : name;
            var value = (byte)opcode;
            nameByValue[value] = name;
            valueByName[name] = value;
            valueByName[bareName] = value;
        }

        // Standard Bitcoin opcodes — pulled from NBitcoin's OpcodeType enum.
        // We only register names that don't collide with Arkade's slots; the
        // 0xb3 slot is intentionally rebound to OP_MERKLEBRANCHVERIFY so it
        // overrides NBitcoin's OP_NOP4.
        foreach (var opcode in Enum.GetValues<NBitcoin.OpcodeType>())
        {
            var rawValue = (int)opcode;
            if (rawValue is < 0 or > byte.MaxValue) continue;
            var b = (byte)rawValue;
            if (nameByValue.ContainsKey(b)) continue;            // Arkade wins on the NOP4 slot
            var name = opcode.ToString();
            // NBitcoin's OpcodeType has multiple aliases for some values
            // (e.g. OP_FALSE/OP_0). Skip later aliases — first wins.
            nameByValue.TryAdd(b, name);
            var bareName = name.StartsWith("OP_") ? name[3..] : name;
            valueByName.TryAdd(name, b);
            valueByName.TryAdd(bareName, b);
        }

        NameByValue = nameByValue.ToFrozenDictionary();
        ValueByName = valueByName.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the canonical name (with <c>OP_</c> prefix) for an opcode byte,
    /// or <c>OP_DATA_N</c> for the data-push range <c>0x01–0x4b</c>, or
    /// <c>null</c> if the byte has no assigned mnemonic.
    /// </summary>
    public static string? GetOpcodeName(byte value)
    {
        if (value is >= 0x01 and <= 0x4b)
            return $"OP_DATA_{value}";
        return NameByValue.TryGetValue(value, out var name) ? name : null;
    }

    /// <summary>
    /// Resolves an opcode name (with or without the <c>OP_</c> prefix, plus the
    /// <c>OP_DATA_N</c> data-push pattern) to its byte value. Returns <c>null</c>
    /// if the name is unknown or the data-push number is out of range.
    /// </summary>
    public static byte? GetOpcodeValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // OP_DATA_N pattern — the data-push opcodes 0x01–0x4b
        if (name.StartsWith("OP_DATA_", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan("OP_DATA_".Length), out var n) &&
            n is >= 1 and <= 75)
        {
            return (byte)n;
        }

        return ValueByName.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>True if the byte value is in the Arkade extension opcode range.</summary>
    public static bool IsArkadeOpcode(byte value)
        => Enum.IsDefined(typeof(ArkadeOpcode), value);
}

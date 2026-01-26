using NBitcoin;

namespace NArk.Swaps.Utils;

/// <summary>
/// Utility class for parsing Bitcoin scripts to extract timelock information.
/// </summary>
public static class ScriptParser
{
    /// <summary>
    /// Extracts timelock information from a hex-encoded script.
    /// </summary>
    /// <param name="hexScript">The script in hexadecimal format.</param>
    /// <returns>
    /// A tuple containing:
    /// - AbsoluteTimelock: LockTime if OP_CHECKLOCKTIMEVERIFY is found
    /// - RelativeTimelock: Sequence if OP_CHECKSEQUENCEVERIFY is found
    /// </returns>
    public static (LockTime? AbsoluteTimelock, Sequence? RelativeTimelock) ExtractTimelocks(string hexScript)
    {
        var script = Script.FromHex(hexScript);
        return ExtractTimelocks(script);
    }

    /// <summary>
    /// Extracts timelock information from a script.
    /// </summary>
    public static (LockTime? AbsoluteTimelock, Sequence? RelativeTimelock) ExtractTimelocks(Script script)
    {
        LockTime? absoluteTimelock = null;
        Sequence? relativeTimelock = null;

        var ops = script.ToOps().ToList();

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];

            // Look for OP_CHECKLOCKTIMEVERIFY (absolute timelock)
            if (op.Code == OpcodeType.OP_CHECKLOCKTIMEVERIFY && i > 0)
            {
                var prevOp = ops[i - 1];
                if (TryGetPushNumber(prevOp, out var lockTimeValue))
                {
                    absoluteTimelock = new LockTime((uint)lockTimeValue);
                }
            }

            // Look for OP_CHECKSEQUENCEVERIFY (relative timelock)
            if (op.Code == OpcodeType.OP_CHECKSEQUENCEVERIFY && i > 0)
            {
                var prevOp = ops[i - 1];
                if (TryGetPushNumber(prevOp, out var sequenceValue))
                {
                    relativeTimelock = new Sequence((uint)sequenceValue);
                }
            }
        }

        return (absoluteTimelock, relativeTimelock);
    }

    /// <summary>
    /// Extracts an absolute timelock (CLTV) from a script leaf.
    /// </summary>
    public static LockTime? ExtractAbsoluteTimelock(string? hexScript)
    {
        if (string.IsNullOrEmpty(hexScript))
            return null;

        var (absoluteTimelock, _) = ExtractTimelocks(hexScript);
        return absoluteTimelock;
    }

    /// <summary>
    /// Extracts a relative timelock (CSV) from a script leaf.
    /// </summary>
    public static Sequence? ExtractRelativeTimelock(string? hexScript)
    {
        if (string.IsNullOrEmpty(hexScript))
            return null;

        var (_, relativeTimelock) = ExtractTimelocks(hexScript);
        return relativeTimelock;
    }

    /// <summary>
    /// Tries to get a number from a push operation.
    /// Handles both small integers (OP_0 through OP_16) and explicit data pushes.
    /// </summary>
    private static bool TryGetPushNumber(Op op, out long value)
    {
        value = 0;

        // Handle OP_0 through OP_16
        if (op.Code >= OpcodeType.OP_0 && op.Code <= OpcodeType.OP_16)
        {
            value = (int)op.Code - (int)OpcodeType.OP_0;
            return true;
        }

        // Handle OP_1NEGATE
        if (op.Code == OpcodeType.OP_1NEGATE)
        {
            value = -1;
            return true;
        }

        // Handle data pushes
        if (op.PushData != null && op.PushData.Length > 0 && op.PushData.Length <= 8)
        {
            // Bitcoin uses little-endian for script numbers
            value = ReadScriptNumber(op.PushData);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a Bitcoin script number from bytes.
    /// Script numbers are little-endian with sign bit.
    /// </summary>
    private static long ReadScriptNumber(byte[] data)
    {
        if (data.Length == 0)
            return 0;

        // Handle negative numbers (sign bit is the MSB of the last byte)
        bool negative = (data[data.Length - 1] & 0x80) != 0;

        long result = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (i == data.Length - 1 && negative)
            {
                // Clear the sign bit for the last byte
                b &= 0x7F;
            }
            result |= (long)b << (8 * i);
        }

        return negative ? -result : result;
    }

    /// <summary>
    /// Decodes a BIP68 sequence value to determine if it's time-based or block-based.
    /// </summary>
    /// <param name="sequence">The sequence value.</param>
    /// <returns>
    /// A tuple containing:
    /// - IsTimeBased: True if the sequence represents time (seconds), false if blocks
    /// - Value: The actual value (blocks or 512-second units)
    /// </returns>
    public static (bool IsTimeBased, int Value) DecodeBip68Sequence(Sequence sequence)
    {
        const uint TYPE_FLAG = 1 << 22; // BIP68 type flag
        const uint VALUE_MASK = 0xFFFF; // Lower 16 bits

        var rawValue = sequence.Value;
        bool isTimeBased = (rawValue & TYPE_FLAG) != 0;
        int value = (int)(rawValue & VALUE_MASK);

        return (isTimeBased, value);
    }
}

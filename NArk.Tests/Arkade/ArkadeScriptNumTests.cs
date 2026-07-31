using System.Numerics;
using NArk.Arkade.Scripts;

namespace NArk.Tests.Arkade;

/// <summary>
/// Round-trip vectors for <see cref="ArkadeScriptNum"/>. Targets the same
/// sign-magnitude little-endian encoding Bitcoin's <c>CScriptNum::serialize</c>
/// (and <c>@scure/btc-signer</c>'s <c>ScriptNum()</c>) emits, lifted to
/// arbitrary <see cref="BigInteger"/> magnitudes so 32-byte EC scalars stay
/// reversible.
/// </summary>
[TestFixture]
public class ArkadeScriptNumTests
{
    private static readonly (BigInteger Value, byte[] Bytes)[] KnownVectors =
    {
        (BigInteger.Zero, []),
        (1, [0x01]),
        (-1, [0x81]),
        (127, [0x7f]),
        (-127, [0xff]),
        (128, [0x80, 0x00]),
        (-128, [0x80, 0x80]),
        (255, [0xff, 0x00]),
        (-255, [0xff, 0x80]),
        (256, [0x00, 0x01]),
        (-256, [0x00, 0x81]),
        (32767, [0xff, 0x7f]),
        (-32767, [0xff, 0xff]),
        (32768, [0x00, 0x80, 0x00]),
        (-32768, [0x00, 0x80, 0x80]),
    };

    [Test]
    public void Encode_KnownVectors()
    {
        foreach (var (value, expected) in KnownVectors)
        {
            var bytes = ArkadeScriptNum.Encode(value);
            Assert.That(bytes, Is.EqualTo(expected),
                $"Encode({value}) → {Convert.ToHexString(bytes)} but expected {Convert.ToHexString(expected)}");
        }
    }

    [Test]
    public void Decode_KnownVectors()
    {
        foreach (var (expected, bytes) in KnownVectors)
        {
            var value = ArkadeScriptNum.Decode(bytes);
            Assert.That(value, Is.EqualTo(expected),
                $"Decode({Convert.ToHexString(bytes)}) → {value} but expected {expected}");
        }
    }

    [Test]
    public void RoundTrip_LargeMagnitudes()
    {
        // 32-byte EC scalar — the use-case OP_ECMULSCALARVERIFY / OP_TWEAKVERIFY
        // care about. Round-trip must be exact.
        var bytes = new byte[32];
        new Random(42).NextBytes(bytes);
        bytes[31] &= 0x7f; // ensure positive (clear sign bit so we don't lose it on encode)
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);

        var encoded = ArkadeScriptNum.Encode(value);
        var decoded = ArkadeScriptNum.Decode(encoded);
        Assert.That(decoded, Is.EqualTo(value));
    }

    [Test]
    public void Decode_NonMinimal_Throws_WhenRequired()
    {
        // [0x00, 0x00] — value zero with a redundant byte (minimal is []).
        Assert.Throws<ArgumentException>(() => ArkadeScriptNum.Decode([0x00, 0x00]));
        // [0x01, 0x00] — value 1 with a redundant zero byte (minimal is [0x01]).
        Assert.Throws<ArgumentException>(() => ArkadeScriptNum.Decode([0x01, 0x00]));
        // [0x01, 0x80] — value -1 with a redundant sign byte (minimal is [0x81]).
        Assert.Throws<ArgumentException>(() => ArkadeScriptNum.Decode([0x01, 0x80]));
    }

    [Test]
    public void Decode_NonMinimal_Allowed_WhenFlagOff()
    {
        Assert.That(ArkadeScriptNum.Decode([0x00, 0x00], requireMinimal: false), Is.EqualTo(BigInteger.Zero));
    }
}

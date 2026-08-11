using NArk.ArkadeIntents.Lightning;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Reading the emulator's key, which the covenant then commits to as a co-signer.
/// </summary>
/// <remarks>
/// Worth its own fixture because the failure is silent. Slicing a prefix off any 33 bytes yields
/// something shaped exactly like a key, and a covenant built on it is a well-formed script pinned to
/// a co-signer nobody holds — an address that looks ordinary right up until nothing can spend it.
/// </remarks>
[TestFixture]
public class LightningCorridorKeyTests
{
    private static readonly byte[] Compressed = Convert.FromHexString(
        "02999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9");

    [Test]
    public void ACompressedKey_BecomesItsXOnlyForm()
    {
        var xOnly = LightningCorridor.NormalizeToXOnly(Compressed);

        Assert.That(Convert.ToHexString(xOnly.ToBytes()).ToLowerInvariant(),
            Is.EqualTo(Convert.ToHexString(Compressed[1..]).ToLowerInvariant()));
    }

    [Test]
    public void AnXOnlyKey_IsTakenAsIs()
    {
        var xOnly = LightningCorridor.NormalizeToXOnly(Compressed[1..]);

        Assert.That(xOnly.ToBytes(), Is.EqualTo(Compressed[1..]));
    }

    [Test]
    public void ThirtyThreeBytesThatAreNotAKey_AreRefused()
    {
        // The case a length check alone waves through: 0x04 opens an uncompressed point, so these
        // are the first 33 bytes of something twice as long. Slicing would hand back 32 bytes that
        // parse fine and mean nothing.
        var truncatedUncompressed = new byte[33];
        truncatedUncompressed[0] = 0x04;
        Compressed[1..].CopyTo(truncatedUncompressed, 1);

        Assert.Throws<ArgumentException>(() => LightningCorridor.NormalizeToXOnly(truncatedUncompressed));
    }

    [Test]
    public void ACompressedPrefixOverGarbage_IsRefused()
    {
        // Right length, right prefix, not a point on the curve.
        var notOnCurve = new byte[33];
        notOnCurve[0] = 0x02;
        Array.Fill(notOnCurve, (byte)0xff, 1, 32);

        Assert.Throws<ArgumentException>(() => LightningCorridor.NormalizeToXOnly(notOnCurve));
    }

    [TestCase(0)]
    [TestCase(31)]
    [TestCase(64)]
    public void AnyOtherLength_IsRefused(int length) =>
        Assert.Throws<ArgumentException>(() => LightningCorridor.NormalizeToXOnly(new byte[length]));
}

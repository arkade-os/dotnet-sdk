using NBitcoin;

namespace NArk.Tests.Arkade;

/// <summary>
/// How a one-byte value is pushed, which decides an address.
/// </summary>
/// <remarks>
/// <para>
/// Arkade scripts are tagged-hashed into the emulator's co-signing key, so their bytes reach the
/// covenant's two non-interactive leaves and from there the taproot output key. A change in how a
/// single byte is pushed changes the address a swap is funded at, and nothing between here and a
/// counterparty's failed claim would notice.
/// </para>
/// <para>
/// The reference SDK spent a release disagreeing with this: it pushed one-byte values raw until
/// arkade-os/ts-sdk#742 moved the minimal-push rule onto the byte branch, because the Arkade VM
/// enforces MINIMALDATA and was rejecting the old encoding. NBitcoin has always canonicalised the
/// same way, so the two agree today — by their change, not ours.
/// </para>
/// <para>
/// Which makes this worth pinning rather than assuming: the agreement rests on a library
/// convention we do not control, and it is asserted here as bytes so a change to it fails in a
/// test rather than at a funded address.
/// </para>
/// </remarks>
[TestFixture]
public class MinimalPushEncodingTests
{
    [TestCase(0x01, "51")]
    [TestCase(0x02, "52")]
    [TestCase(0x10, "60")]
    public void OneByteValuesOneThroughSixteen_BecomeTheirOpcodes(byte value, string expected)
    {
        Assert.That(Push(value), Is.EqualTo(expected));
    }

    [Test]
    public void TheByteEightyOne_BecomesOneNegate()
    {
        Assert.That(Push(0x81), Is.EqualTo("4f"));
    }

    [Test]
    public void TheZeroByte_KeepsItsLengthPrefixedPush()
    {
        // Deliberately NOT OP_0: an empty push and a push of one zero byte are different stack
        // items, and collapsing them would change the script rather than shorten it.
        Assert.That(Push(0x00), Is.EqualTo("0100"));
    }

    [Test]
    public void AByteAboveSixteen_KeepsItsLengthPrefixedPush()
    {
        // No opcode form exists above OP_16, so this is the ordinary encoding and the boundary
        // where the special cases stop.
        Assert.That(Push(0x11), Is.EqualTo("0111"));
    }

    [Test]
    public void ThirtyTwoBytes_ArePushedWithTheirLength()
    {
        // The size every witness program and hash in the covenant uses; a change here would move
        // every leaf at once rather than only the ones carrying small numbers.
        var thirtyTwo = Enumerable.Repeat((byte)0xab, 32).ToArray();

        Assert.That(new Script(Op.GetPushOp(thirtyTwo)).ToHex(), Does.StartWith("20"));
    }

    private static string Push(byte value) => new Script(Op.GetPushOp([value])).ToHex();
}

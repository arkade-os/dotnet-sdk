using NArk.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// The messages a claim preimage is derived from.
/// </summary>
/// <remarks>
/// <para>
/// These bytes decide a secret, and a secret that cannot be reproduced is money that dies with a
/// database. They are pinned against the counterparty's layout rather than against our own output,
/// because the property worth having is that both sides compute the same thing — asserting that
/// our code equals our code would hold just as well while being wrong.
/// </para>
/// <para>
/// The salted arm exists because a single-key wallet has one key. Pinning an index there would
/// give every swap the identical preimage, and one counterparty learning its own would learn all
/// of them.
/// </para>
/// </remarks>
[TestFixture]
public class PreimageProvisioningTests
{
    private static readonly byte[] XOnly =
        Convert.FromHexString("55355ca83c973f1d97ce0e3843c85d78905af16b4dc531bc488e57212d230116");

    [Test]
    public void TheUnsaltedMessage_IsTagThenKeyThenLittleEndianIndex()
    {
        var message = PreimageProvisioning.BuildPreimageMessage(XOnly, 1);

        var tag = "Arkade-RFQ-Preimage-v1"u8.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(message[..tag.Length], Is.EqualTo(tag));
            Assert.That(message[tag.Length..(tag.Length + 32)], Is.EqualTo(XOnly));
            // Little-endian, and pinned as bytes: a big-endian index would still be four bytes and
            // still produce a preimage, just never the same one twice across implementations.
            Assert.That(message[(tag.Length + 32)..], Is.EqualTo(new byte[] { 1, 0, 0, 0 }));
            Assert.That(message, Has.Length.EqualTo(tag.Length + 36));
        });
    }

    [Test]
    public void TheSaltedMessage_IsTagThenKeyThenSalt()
    {
        var salt = Enumerable.Repeat((byte)0xab, 32).ToArray();

        var message = PreimageProvisioning.BuildSaltedPreimageMessage(XOnly, salt);

        var tag = "Arkade-Contract-Preimage-Salted-v1"u8.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(message[..tag.Length], Is.EqualTo(tag));
            Assert.That(message[tag.Length..(tag.Length + 32)], Is.EqualTo(XOnly));
            Assert.That(message[(tag.Length + 32)..], Is.EqualTo(salt));
        });
    }

    [Test]
    public void TheTwoTags_AreDistinct()
    {
        // Sharing a tag would let one arm's message be reinterpreted as the other's, which is the
        // whole reason a domain tag exists.
        Assert.That(PreimageProvisioning.PreimageTag, Is.Not.EqualTo(PreimageProvisioning.SaltedPreimageTag));
    }

    [Test]
    public void TheSaltedMessage_ChangesWithTheSalt()
    {
        // The property the salted arm exists for: one key, many swaps, no two alike.
        var first = PreimageProvisioning.BuildSaltedPreimageMessage(XOnly, new byte[32]);
        var second = PreimageProvisioning.BuildSaltedPreimageMessage(
            XOnly, Enumerable.Repeat((byte)1, 32).ToArray());

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [TestCase(31)]
    [TestCase(33)]
    public void AKeyOfTheWrongLength_IsRefused(int length)
    {
        Assert.Throws<ArgumentException>(
            () => PreimageProvisioning.BuildPreimageMessage(new byte[length], 0));
    }

    [Test]
    public void ASaltOfTheWrongLength_IsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => PreimageProvisioning.BuildSaltedPreimageMessage(XOnly, new byte[16]));
    }

    [Test]
    public void AnHdDescriptor_TakesTheUnsaltedArm()
    {
        var hd = OutputDescriptor.Parse(
            "tr([abcd1234/86'/1'/0']tpubDCNCPsBf7i2ocZm1nFqDwHCCaDaq56Ugtc7ZrV6XbHjvD4o2NiR9bB348zbx1XCJcZEQkEY8hZx3U2ZST9roWLQ8dvJS6Za6TSX7HCvsBDK/0/*)",
            Network.RegTest, requireCheckSum: false);

        Assert.That(PreimageProvisioning.IsPerArtifactDescriptor(hd), Is.True);
    }

    [Test]
    public void ABareKeyWithOriginMetadata_StillTakesTheSaltedArm()
    {
        // The trap: a key-origin path makes the descriptor LOOK derived while it is one key
        // forever. Reading it as per-artifact would give every swap on this wallet the same
        // preimage, and one counterparty learning its own would learn all of them.
        var withOrigin = OutputDescriptor.Parse(
            $"tr([abcd1234/86'/1'/0']{new Key().PubKey.ToHex()})", Network.RegTest, requireCheckSum: false);

        Assert.That(PreimageProvisioning.IsPerArtifactDescriptor(withOrigin), Is.False);
    }

    [Test]
    public void AConcreteHdChild_TakesTheUnsaltedArm()
    {
        // A fresh child per swap is what makes the pinned index safe.
        var child = OutputDescriptor.Parse(
            "tr([abcd1234/86'/1'/0']tpubDCNCPsBf7i2ocZm1nFqDwHCCaDaq56Ugtc7ZrV6XbHjvD4o2NiR9bB348zbx1XCJcZEQkEY8hZx3U2ZST9roWLQ8dvJS6Za6TSX7HCvsBDK/0/5)",
            Network.RegTest, requireCheckSum: false);

        Assert.That(PreimageProvisioning.IsPerArtifactDescriptor(child), Is.True);
    }

    [Test]
    public void ABareKeyDescriptor_TakesTheSaltedArm()
    {
        // One key for every swap, so the index cannot be what makes a preimage unique.
        var bare = OutputDescriptor.Parse(
            $"tr({new Key().PubKey.ToHex()})", Network.RegTest, requireCheckSum: false);

        Assert.That(PreimageProvisioning.IsPerArtifactDescriptor(bare), Is.False);
    }
}

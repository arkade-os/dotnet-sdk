using System.Text;
using NArk.ArkadeIntents.Nostr;

namespace NArk.Tests.ArkadeIntents.Nostr;

/// <summary>
/// ChaCha20 against RFC 8439's own vectors.
/// </summary>
/// <remarks>
/// The NIP-44 vector exercises this cipher too, but only at one key, one nonce and one length —
/// which leaves the parts most likely to be wrong in a hand-written implementation untested: the
/// block counter advancing across a boundary, a non-zero initial counter, and a final partial
/// block. Those are exactly the cases that produce output which is correct for short inputs and
/// silently wrong for longer ones.
/// </remarks>
[TestFixture]
public class ChaCha20Tests
{
    /// <summary>RFC 8439 §2.4.2 — key 00..1f, a 114-byte plaintext, initial counter 1.</summary>
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static readonly byte[] Nonce =
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4a, 0x00, 0x00, 0x00, 0x00];

    private const string Plaintext =
        "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.";

    private const string ExpectedCiphertext =
        "6e2e359a2568f98041ba0728dd0d6981e97e7aec1d4360c20a27afccfd9fae0bf91b65c5524733ab8f593dabcd62b357" +
        "1639d624e65152ab8f530c359f0861d807ca0dbf500d6a6156a38e088a22b65e52bc514d16ccf806818ce91ab7793736" +
        "5af90bbf74a35be6b40b8eedf2785e42874d";

    [Test]
    public void Rfc8439_Section242_Vector()
    {
        var data = Encoding.ASCII.GetBytes(Plaintext);

        ChaCha20.XorKeyStream(Key, Nonce, data, counter: 1);

        Assert.That(Convert.ToHexString(data).ToLowerInvariant(), Is.EqualTo(ExpectedCiphertext));
    }

    [Test]
    public void Rfc8439_Section242_RoundTrips()
    {
        // The cipher is its own inverse, so decryption is the same call. Worth asserting rather than
        // assuming, since an asymmetric bug would still pass the vector above.
        var data = Convert.FromHexString(ExpectedCiphertext);

        ChaCha20.XorKeyStream(Key, Nonce, data, counter: 1);

        Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo(Plaintext));
    }

    [Test]
    public void TheVector_CrossesABlockBoundary()
    {
        // 114 bytes is one full 64-byte block plus a 50-byte tail, so the counter has to advance
        // once and the last block has to be truncated. That is the case a naive implementation gets
        // wrong while still passing anything shorter than 64 bytes.
        Assert.That(Encoding.ASCII.GetByteCount(Plaintext), Is.EqualTo(114));
    }

    [Test]
    public void CounterAdvances_SoTheSecondBlockDiffersFromStartingThere()
    {
        // Encrypting 128 zero bytes from counter 0 must produce, in its second half, exactly what
        // encrypting 64 zero bytes from counter 1 produces. If the counter did not advance per
        // block, every block would repeat the first one's keystream.
        var twoBlocks = new byte[128];
        ChaCha20.XorKeyStream(Key, Nonce, twoBlocks);

        var secondAlone = new byte[64];
        ChaCha20.XorKeyStream(Key, Nonce, secondAlone, counter: 1);

        Assert.That(twoBlocks[64..], Is.EqualTo(secondAlone));
    }

    [Test]
    public void APartialFinalBlock_MatchesThePrefixOfAFullOne()
    {
        var partial = new byte[37];
        ChaCha20.XorKeyStream(Key, Nonce, partial, counter: 7);

        var full = new byte[64];
        ChaCha20.XorKeyStream(Key, Nonce, full, counter: 7);

        Assert.That(partial, Is.EqualTo(full[..37]));
    }

    [TestCase(31)]
    [TestCase(33)]
    public void WrongKeyLength_IsRefused(int length)
    {
        Assert.Throws<ArgumentException>(() =>
            ChaCha20.XorKeyStream(new byte[length], Nonce, new byte[16]));
    }

    [TestCase(11)]
    [TestCase(13)]
    public void WrongNonceLength_IsRefused(int length)
    {
        Assert.Throws<ArgumentException>(() =>
            ChaCha20.XorKeyStream(Key, new byte[length], new byte[16]));
    }
}

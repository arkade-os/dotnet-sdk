using System.Text;
using System.Text.Json;
using NArk.ArkadeIntents.Nostr;

namespace NArk.Tests.ArkadeIntents.Nostr;

/// <summary>
/// ChaCha20 against RFC 8439's own vectors and a broad cross-checked set.
/// </summary>
/// <remarks>
/// <para>
/// The NIP-44 cross-check exercises this cipher too, but only at one key, one nonce and one length.
/// That leaves the parts most likely to be wrong in a hand-written implementation untested: the
/// block counter advancing across a boundary, a non-zero initial counter, and a truncated final
/// block. Each produces output that is correct for short inputs and silently wrong for longer ones,
/// which is the worst failure shape a cipher has — the ciphertext still looks like noise either way.
/// </para>
/// <para>
/// So there are three layers here. RFC 8439's Appendix A.2 is the authority, parsed from the
/// published text rather than retyped. Around it sits a generated set spanning thirty lengths
/// clustered on the 64-byte seams and seven counters up to the 32-bit ceiling, cross-checked against
/// the implementation the reference solver itself seals with. The rest assert properties no single
/// vector can: that decryption is the same operation, that block N of a long run equals a run
/// started at block N, and that a partial block is a prefix of the full one.
/// </para>
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

    [TestCaseSource(nameof(Rfc8439AppendixA2))]
    public void Rfc8439_AppendixA2(Vector v)
    {
        // Parsed from the published RFC text rather than retyped, and the three between them cover a
        // single whole block, a 375-byte run from counter 1, and a 127-byte run from counter 42.
        var data = Convert.FromHexString(v.Plaintext);

        ChaCha20.XorKeyStream(
            Convert.FromHexString(v.Key), Convert.FromHexString(v.Nonce), data, (uint)v.Counter);

        Assert.That(Convert.ToHexString(data).ToLowerInvariant(), Is.EqualTo(v.Ciphertext));
    }

    [TestCaseSource(nameof(GeneratedVectors))]
    public void CrossChecked_AgainstTheReferenceImplementation(Vector v)
    {
        var data = Convert.FromHexString(v.Plaintext);

        ChaCha20.XorKeyStream(
            Convert.FromHexString(v.Key), Convert.FromHexString(v.Nonce), data, (uint)v.Counter);

        Assert.That(Convert.ToHexString(data).ToLowerInvariant(), Is.EqualTo(v.Ciphertext));
    }

    [Test]
    public void TheGeneratedSet_CoversTheSeams()
    {
        // A guard on the fixture itself: if a regeneration ever narrowed the spread, the suite would
        // still pass while testing far less than it looks like it does.
        var all = Fixture.Vectors;
        var lengths = all.Select(v => v.Plaintext.Length / 2).ToHashSet();
        var counters = all.Select(v => v.Counter).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(all, Has.Count.GreaterThan(100));
            Assert.That(lengths, Does.Contain(0).And.Contains(63).And.Contains(64).And.Contains(65));
            Assert.That(counters, Does.Contain(0L).And.Contains(1L));
            Assert.That(counters.Max(), Is.GreaterThan(4_000_000_000L), "a counter near the 32-bit ceiling");
            Assert.That(lengths.Max(), Is.GreaterThanOrEqualTo(4096));
        });
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

    // ─── Fixture ──────────────────────────────────────────────────────

    private static readonly Fixtures Fixture = LoadFixture();

    public static IEnumerable<TestCaseData> Rfc8439AppendixA2() =>
        Fixture.Rfc8439.Vectors.Select((v, i) => new TestCaseData(v).SetName($"RFC 8439 A.2 #{i + 1}"));

    public static IEnumerable<TestCaseData> GeneratedVectors() =>
        Fixture.Vectors.Select(v =>
            new TestCaseData(v).SetName($"len {v.Plaintext.Length / 2}, counter {v.Counter}"));

    private static Fixtures LoadFixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory, "ArkadeIntents", "Fixtures", "chacha20.json");
        return JsonSerializer.Deserialize<Fixtures>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record Fixtures(List<Vector> Vectors, RfcSet Rfc8439);

    public sealed record RfcSet(List<Vector> Vectors);

    public sealed record Vector(string Key, string Nonce, long Counter, string Plaintext, string Ciphertext);
}

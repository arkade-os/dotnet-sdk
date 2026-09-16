using System.Security.Cryptography;
using System.Text.Json;
using NArk.ArkadeIntents.Nostr;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents.Nostr;

/// <summary>
/// Pins NIP-44 v2 to nostr-tools, the library the reference solver seals its traffic with.
/// </summary>
/// <remarks>
/// This is a from-scratch implementation down to a hand-written ChaCha20, because the platform
/// exposes only the AEAD construction and NIP-44 uses the bare stream. Conversation key, padding
/// buckets, HKDF expansion, the MAC's associated data — every one of those is somewhere two
/// implementations can differ while each looks correct alone. The symptom would be a solver that
/// silently ignores us: on a shared relay a payload it cannot open is indistinguishable from one
/// that was never addressed to it.
/// </remarks>
[TestFixture]
public class Nip44Tests
{
    private static readonly Vectors Fixture = LoadFixture();

    [Test]
    public void ConversationKey_MatchesNostrTools()
    {
        Assert.That(Hex(ClientSideKey()), Is.EqualTo(Fixture.ConversationKey));
    }

    [Test]
    public void ConversationKey_IsSymmetric()
    {
        // Each side derives it from its own secret and the other's public key. If that failed, one
        // side would encrypt into a key the other never computes.
        var solverSide = Nip44.GetConversationKey(
            new Key(Convert.FromHexString(Fixture.Inputs.SolverPrivateKey)),
            Convert.FromHexString(Fixture.Inputs.ClientPublicKey));

        Assert.That(Hex(solverSide), Is.EqualTo(Fixture.ConversationKey));
    }

    [Test]
    public void Encrypt_ReproducesTheReferencePayloadByteForByte()
    {
        var payload = Nip44.Encrypt(
            Fixture.Inputs.Plaintext, ClientSideKey(), Convert.FromHexString(Fixture.Inputs.Nonce));

        Assert.That(payload, Is.EqualTo(Fixture.Payload));
    }

    [Test]
    public void Decrypt_ReadsWhatTheReferenceProduced()
    {
        Assert.That(Nip44.Decrypt(Fixture.Payload, ClientSideKey()), Is.EqualTo(Fixture.Inputs.Plaintext));
    }

    [Test]
    public void PaddingBuckets_MatchNostrTools()
    {
        // These decide the ciphertext length, so a disagreement changes every payload's size and
        // breaks the MAC before anything else gets a chance to.
        Assert.Multiple(() =>
        {
            foreach (var (unpadded, expected) in Fixture.PaddedLengths)
            {
                Assert.That(Nip44.CalcPaddedLength(int.Parse(unpadded)), Is.EqualTo(expected),
                    $"bucket for {unpadded} bytes");
            }
        });
    }

    [Test]
    public void Decrypt_RefusesATamperedPayload()
    {
        // Flip a byte inside the ciphertext. The MAC has to catch it before the length prefix is
        // read, since that prefix is exactly what a forger would aim at.
        var raw = Convert.FromBase64String(Fixture.Payload);
        raw[40] ^= 0xff;

        Assert.Throws<CryptographicException>(() =>
            Nip44.Decrypt(Convert.ToBase64String(raw), ClientSideKey()));
    }

    [Test]
    public void Decrypt_RefusesAnUnknownVersion()
    {
        var raw = Convert.FromBase64String(Fixture.Payload);
        raw[0] = 0x01;

        Assert.Throws<CryptographicException>(() =>
            Nip44.Decrypt(Convert.ToBase64String(raw), ClientSideKey()));
    }

    [Test]
    public void RoundTrip_SurvivesAFreshRandomNonce()
    {
        var key = ClientSideKey();
        var nonce = RandomNumberGenerator.GetBytes(32);

        var payload = Nip44.Encrypt("a quote worth sealing", key, nonce);

        Assert.That(Nip44.Decrypt(payload, key), Is.EqualTo("a quote worth sealing"));
    }

    private static byte[] ClientSideKey() => Nip44.GetConversationKey(
        new Key(Convert.FromHexString(Fixture.Inputs.ClientPrivateKey)),
        Convert.FromHexString(Fixture.Inputs.SolverPublicKey));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static Vectors LoadFixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory, "ArkadeIntents", "Fixtures", "nip44.json");
        return JsonSerializer.Deserialize<Vectors>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record Vectors(
        VectorInputs Inputs,
        string ConversationKey,
        Dictionary<string, int> PaddedLengths,
        string Payload);

    public sealed record VectorInputs(
        string ClientPrivateKey,
        string ClientPublicKey,
        string SolverPrivateKey,
        string SolverPublicKey,
        string Nonce,
        string Plaintext);
}

using System.Text.Json;
using NArk.ArkadeIntents.Lightning;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Pins the receive legs' ECIES claim packet to the counterparty's own construction.
/// </summary>
/// <remarks>
/// Only covclaimd holds the key that opens this, so we cannot check our output by decrypting it.
/// Instead every input is fixed — including the ephemeral key and nonce, which the real API
/// deliberately never lets a caller supply — and the bytes must equal the reference
/// implementation's. That catches the one mistake the scheme invites: the ECDH shared secret is the
/// 32-byte X coordinate, and keeping the compressed point's parity byte still derives a well-formed
/// key at both ends. Nothing local disagrees; only a live daemon's tag check does, and by then a
/// swap is already in flight.
/// </remarks>
[TestFixture]
public class ClaimPacketTests
{
    private static readonly Vectors Fixture = LoadFixture();

    [Test]
    public void Seal_MatchesTheReferenceConstruction()
    {
        var sealed_ = Seal();

        Assert.That(sealed_.Packet, Is.EqualTo(Fixture.Packet));
    }

    [Test]
    public void Seal_DerivesThePaymentHashTheQuoteIsRequestedAgainst()
    {
        Assert.That(Seal().PaymentHash, Is.EqualTo(Fixture.PaymentHash));
    }

    [Test]
    public void Packet_CarriesTheEphemeralKeyAndNonceInTheClear()
    {
        // covclaimd needs both to derive the same key, so they ride unencrypted ahead of the
        // ciphertext: ephPub(33) || nonce(12) || ciphertext+tag.
        var wire = Convert.FromBase64String(Seal().Packet);

        Assert.Multiple(() =>
        {
            Assert.That(Hex(wire[..33]), Is.EqualTo(Fixture.Intermediates.EphemeralPublicKey));
            Assert.That(Hex(wire[33..45]), Is.EqualTo(Fixture.Inputs.Nonce));
            // 32-byte preimage plus GCM's 16-byte tag.
            Assert.That(wire.Length, Is.EqualTo(33 + 12 + 32 + 16));
        });
    }

    [Test]
    public void Seal_RejectsAPreimageThatIsNotThirtyTwoBytes()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ClaimPacket.Seal(new byte[31], Fixture.Inputs.CovclaimdPublicKey, Ephemeral(), Nonce()));

        Assert.That(ex!.Message, Does.Contain("32 bytes"));
    }

    [Test]
    public void New_ProducesADifferentPacketEveryTime()
    {
        // The ephemeral key and nonce are per-packet. Reusing either would break GCM outright, so
        // two seals of the same secret must still differ.
        var first = ClaimPacket.New(Fixture.Inputs.CovclaimdPublicKey);
        var second = ClaimPacket.New(Fixture.Inputs.CovclaimdPublicKey);

        Assert.That(first.Packet, Is.Not.EqualTo(second.Packet));
    }

    private static SealedClaimPacket Seal() => ClaimPacket.Seal(
        Convert.FromHexString(Fixture.Inputs.Preimage),
        Fixture.Inputs.CovclaimdPublicKey,
        Ephemeral(),
        Nonce());

    private static Key Ephemeral() => new(Convert.FromHexString(Fixture.Inputs.EphemeralPrivateKey));

    private static byte[] Nonce() => Convert.FromHexString(Fixture.Inputs.Nonce);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static Vectors LoadFixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "ArkadeIntents", "Fixtures", "claim_packet.json");
        return JsonSerializer.Deserialize<Vectors>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record Vectors(
        VectorInputs Inputs,
        VectorIntermediates Intermediates,
        string Packet,
        string PaymentHash);

    public sealed record VectorInputs(
        string Preimage,
        string EphemeralPrivateKey,
        string CovclaimdPublicKey,
        string Nonce);

    public sealed record VectorIntermediates(
        string EphemeralPublicKey,
        string SharedSecretX,
        string DerivedKey);
}

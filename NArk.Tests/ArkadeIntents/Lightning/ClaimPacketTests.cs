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
    public void Packet_FramesTheCiphertextKeyAndAmountAsCovclaimdTlvs()
    {
        // 0x02, the arkade script, is the solver's to append.
        var packet = Convert.FromBase64String(Seal().Packet);
        var ciphertext = Convert.FromBase64String(Fixture.Ciphertext);

        Assert.Multiple(() =>
        {
            Assert.That(Hex(packet[..3]), Is.EqualTo("01005d"));
            Assert.That(packet[3..96], Is.EqualTo(ciphertext));
            Assert.That(Hex(ciphertext[..33]), Is.EqualTo(Fixture.Intermediates.EphemeralPublicKey));
            Assert.That(Hex(ciphertext[33..45]), Is.EqualTo(Fixture.Inputs.Nonce));
            Assert.That(Hex(packet[96..132]), Is.EqualTo("030021" + Fixture.Inputs.CovclaimdPublicKey));
            Assert.That(Hex(packet[132..]), Is.EqualTo("040008" + Fixture.Inputs.MinAmount.ToString("x16")));
        });
    }

    [Test]
    public void Seal_RejectsAPreimageThatIsNotThirtyTwoBytes()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ClaimPacket.SealAsync(new byte[31], Fixture.Inputs.CovclaimdPublicKey, Fixture.Inputs.MinAmount, Ephemeral(), Nonce(), new AesGcmCipher()).GetAwaiter().GetResult());

        Assert.That(ex!.Message, Does.Contain("32 bytes"));
    }

    [Test]
    public void Seal_RejectsAZeroMinAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClaimPacket.SealAsync(Convert.FromHexString(Fixture.Inputs.Preimage), Fixture.Inputs.CovclaimdPublicKey, 0, Ephemeral(), Nonce(), new AesGcmCipher()).GetAwaiter().GetResult());
    }

    [Test]
    public void New_ProducesADifferentPacketEveryTime()
    {
        // The ephemeral key and nonce are per-packet. Reusing either would break GCM outright, so
        // two seals of the same secret must still differ.
        var first = ClaimPacket.NewAsync(Fixture.Inputs.CovclaimdPublicKey, Fixture.Inputs.MinAmount).GetAwaiter().GetResult();
        var second = ClaimPacket.NewAsync(Fixture.Inputs.CovclaimdPublicKey, Fixture.Inputs.MinAmount).GetAwaiter().GetResult();

        Assert.That(first.Packet, Is.Not.EqualTo(second.Packet));
    }

    private static SealedClaimPacket Seal() => SealAsync().GetAwaiter().GetResult();

    private static Task<SealedClaimPacket> SealAsync() => ClaimPacket.SealAsync(
        Convert.FromHexString(Fixture.Inputs.Preimage),
        Fixture.Inputs.CovclaimdPublicKey,
        Fixture.Inputs.MinAmount,
        Ephemeral(),
        Nonce(),
        new AesGcmCipher());

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
        string Ciphertext,
        string Packet,
        string PaymentHash);

    public sealed record VectorInputs(
        string Preimage,
        string EphemeralPrivateKey,
        string CovclaimdPublicKey,
        string Nonce,
        ulong MinAmount);

    public sealed record VectorIntermediates(
        string EphemeralPublicKey,
        string SharedSecretX,
        string DerivedKey);
}

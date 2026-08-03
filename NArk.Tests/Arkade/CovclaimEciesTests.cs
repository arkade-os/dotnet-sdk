using System.Security.Cryptography;
using NArk.Arkade.Covclaim;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Wire-format tests for <see cref="CovclaimEcies"/>.
/// </summary>
/// <remarks>
/// The output is randomised per call (fresh ephemeral key + nonce), so there is
/// no fixed ciphertext to pin. What can be pinned is the envelope layout and the
/// fact that a blob decrypts back to the original bytes under the recipient's
/// key — which together cover the parts covclaimd actually parses.
/// </remarks>
[TestFixture]
public class CovclaimEciesTests
{
    private const int PubKeyLength = 33;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int PreimageLength = 32;

    /// <summary>The fixed regtest key from <c>COVCLAIMD_SECRET_KEY</c> in arkade-regtest.</summary>
    private const string RegtestSecretKeyHex =
        "37999628937d49cfc66e30ac17404dd4cf0414cebaf965d54e6b1b0af1cfe4b0";

    private static ECPrivKey ParsePrivKey(string hex)
    {
        Assert.That(
            ECPrivKey.TryCreate(Convert.FromHexString(hex), Context.Instance, out var key),
            Is.True, "test vector private key should parse");
        return key;
    }

    /// <summary>
    /// Decrypts a blob produced by covclaimd's own <c>preimage.Encrypt</c>.
    /// </summary>
    /// <remarks>
    /// This is the test that actually proves interop. The round-trip test below
    /// only shows this implementation agrees with itself, which it would even if
    /// the ECDH secret used the compressed point instead of the bare
    /// x-coordinate, or the HKDF salt/info differed — all silent, all fatal.
    /// </remarks>
    [Test]
    public void Decrypt_AcceptsGoProducedCiphertext()
    {
        const string goCiphertextHex =
            "035bbaac4f9c60347ee097db1349bfc737db01f9efcd0d2d55a9c067e6e24b2a5d" +
            "3ae06bdff77db9e633e744a6abf6a32c160fc2853c1ec4149accd7fba7fe1a1df5" +
            "45101c1d2c4d06a0aa5bdab80feef4b5f41b82f4d18680bf047608";
        const string expectedPreimageHex =
            "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

        var plaintext = CovclaimEcies.Decrypt(
            ParsePrivKey(RegtestSecretKeyHex), Convert.FromHexString(goCiphertextHex));

        Assert.That(
            Convert.ToHexString(plaintext).ToLowerInvariant(), Is.EqualTo(expectedPreimageHex));
    }

    [Test]
    public void Encrypt_RoundTripsThroughDecrypt()
    {
        var recipient = ParsePrivKey(RegtestSecretKeyHex);
        var preimage = RandomNumberGenerator.GetBytes(PreimageLength);

        var blob = CovclaimEcies.Encrypt(recipient.CreatePubKey(), preimage);

        Assert.That(CovclaimEcies.Decrypt(recipient, blob), Is.EqualTo(preimage));
    }

    [Test]
    public void Encrypt_ProducesExpectedEnvelopeLayout()
    {
        var recipient = ParsePrivKey(RegtestSecretKeyHex);
        var preimage = RandomNumberGenerator.GetBytes(PreimageLength);

        var blob = CovclaimEcies.Encrypt(recipient.CreatePubKey(), preimage);

        Assert.Multiple(() =>
        {
            Assert.That(blob, Has.Length.EqualTo(PubKeyLength + NonceLength + PreimageLength + TagLength));

            // The leading 33 bytes must be a parseable compressed point — covclaimd
            // parses them before it can derive anything.
            Assert.That(
                ECPubKey.TryCreate(blob[..PubKeyLength], Context.Instance, out _, out _),
                Is.True, "envelope should start with a compressed ephemeral pubkey");
        });
    }

    /// <summary>
    /// A fresh ephemeral key per call is what keeps two claims for the same
    /// preimage from being linkable on the wire.
    /// </summary>
    [Test]
    public void Encrypt_IsNonDeterministic()
    {
        var recipient = ParsePrivKey(RegtestSecretKeyHex).CreatePubKey();
        var preimage = RandomNumberGenerator.GetBytes(PreimageLength);

        Assert.That(
            CovclaimEcies.Encrypt(recipient, preimage),
            Is.Not.EqualTo(CovclaimEcies.Encrypt(recipient, preimage)));
    }

    [Test]
    public void Decrypt_WrongRecipientKey_Fails()
    {
        var blob = CovclaimEcies.Encrypt(ParsePrivKey(RegtestSecretKeyHex).CreatePubKey(),
            RandomNumberGenerator.GetBytes(PreimageLength));

        var wrongKey = ParsePrivKey(
            "0000000000000000000000000000000000000000000000000000000000000042");

        Assert.Throws<AuthenticationTagMismatchException>(
            () => CovclaimEcies.Decrypt(wrongKey, blob));
    }

    /// <summary>
    /// A malformed ephemeral pubkey is rejected before any key is derived.
    /// </summary>
    /// <remarks>
    /// The prefix is set to an invalid value rather than flipping a random byte:
    /// corrupting the x-coordinate only sometimes lands off-curve, so a byte flip
    /// there would make this test pass or fail depending on the ephemeral key.
    /// </remarks>
    [Test]
    public void Decrypt_MalformedEphemeralKey_Rejected()
    {
        var recipient = ParsePrivKey(RegtestSecretKeyHex);
        var blob = CovclaimEcies.Encrypt(recipient.CreatePubKey(),
            RandomNumberGenerator.GetBytes(PreimageLength));

        blob[0] = 0x07; // neither 0x02 nor 0x03

        Assert.Throws<ArgumentException>(() => CovclaimEcies.Decrypt(recipient, blob));
    }

    /// <summary>
    /// Substituting a different — but structurally valid — ephemeral key fails
    /// authentication, since that key is both the HKDF salt and the AEAD's
    /// associated data.
    /// </summary>
    /// <remarks>
    /// Flipping the low bit of the prefix negates the point, which always yields a
    /// parseable compressed encoding, so this exercises the AEAD binding rather
    /// than the parser.
    /// </remarks>
    [Test]
    public void Decrypt_SubstitutedEphemeralKey_FailsAuthentication()
    {
        var recipient = ParsePrivKey(RegtestSecretKeyHex);
        var blob = CovclaimEcies.Encrypt(recipient.CreatePubKey(),
            RandomNumberGenerator.GetBytes(PreimageLength));

        blob[0] ^= 0x01; // 0x02 <-> 0x03

        Assert.Throws<AuthenticationTagMismatchException>(
            () => CovclaimEcies.Decrypt(recipient, blob));
    }

    /// <summary>
    /// The nonce and ciphertext are covered by the GCM tag, so any edit to them
    /// must fail authentication rather than yield garbage plaintext.
    /// </summary>
    [TestCase(PubKeyLength, Description = "nonce")]
    [TestCase(PubKeyLength + NonceLength, Description = "ciphertext")]
    public void Decrypt_TamperedPayload_FailsAuthentication(int offset)
    {
        var recipient = ParsePrivKey(RegtestSecretKeyHex);
        var blob = CovclaimEcies.Encrypt(recipient.CreatePubKey(),
            RandomNumberGenerator.GetBytes(PreimageLength));

        blob[offset] ^= 0xff;

        Assert.Throws<AuthenticationTagMismatchException>(
            () => CovclaimEcies.Decrypt(recipient, blob));
    }

    [Test]
    public void Decrypt_TruncatedBlob_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => CovclaimEcies.Decrypt(ParsePrivKey(RegtestSecretKeyHex), new byte[10]));
    }
}

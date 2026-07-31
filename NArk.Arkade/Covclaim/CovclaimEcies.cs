using System.Security.Cryptography;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Covclaim;

/// <summary>
/// ECIES over secp256k1 as spoken by <c>covclaimd</c>: ephemeral ECDH →
/// HKDF-SHA256 → AES-256-GCM. Used to encrypt a swap preimage to the claim
/// daemon's public key so it can claim on our behalf without us handing the
/// preimage to anyone else.
/// </summary>
/// <remarks>
/// <para>Wire format of the returned blob:</para>
/// <code>
/// ephemeralPubKey (33, compressed) || nonce (12) || ciphertext || GCM tag (16)
/// </code>
/// <para>
/// The ephemeral public key doubles as the HKDF salt and the AEAD's associated
/// data, which binds the ciphertext to the key that derived it. The ECDH shared
/// secret is the <em>x-coordinate only</em> (32 bytes, no parity byte) — a
/// common ECIES variation and a silent-failure trap if the other side hashes the
/// compressed point instead.
/// </para>
/// <para>
/// Mirrors covclaimd's <c>pkg/preimage/crypto.go</c>. Only encryption is public:
/// this SDK is a client of the daemon, so it never needs to decrypt someone
/// else's claim packet.
/// </para>
/// </remarks>
public static class CovclaimEcies
{
    private const int PubKeyLength = 33;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = PubKeyLength + NonceLength;

    /// <summary>HKDF <c>info</c> string; must match covclaimd's <c>eciesHkdfInfo</c>.</summary>
    private const string HkdfInfo = "covclaimd/preimage/v1";

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> to <paramref name="recipient"/>.
    /// </summary>
    /// <param name="recipient">The daemon's public key, from <c>GetCovclaimdPubKey</c>.</param>
    /// <param name="plaintext">Payload to encrypt — a 32-byte swap preimage in practice.</param>
    /// <returns>The ECIES blob described in the remarks on this class.</returns>
    public static byte[] Encrypt(ECPubKey recipient, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        var ephemeral = CreateEphemeralKey();
        var ephemeralPubKey = ephemeral.CreatePubKey().ToBytes(compressed: true);

        var symmetricKey = DeriveSymmetricKey(ephemeral, recipient, salt: ephemeralPubKey);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aead = new AesGcm(symmetricKey, TagLength);
        aead.Encrypt(nonce, plaintext, ciphertext, tag, associatedData: ephemeralPubKey);

        return [.. ephemeralPubKey, .. nonce, .. ciphertext, .. tag];
    }

    /// <summary>
    /// Decrypts a blob produced by <see cref="Encrypt"/>. Internal because the
    /// SDK is a client and never holds the daemon's key in production — this
    /// exists so tests can verify the wire format round-trips, which is the only
    /// way to check <see cref="Encrypt"/> against a spec whose output is
    /// randomised per call.
    /// </summary>
    internal static byte[] Decrypt(ECPrivKey recipientKey, ReadOnlySpan<byte> blob)
    {
        ArgumentNullException.ThrowIfNull(recipientKey);
        if (blob.Length < HeaderLength + TagLength)
            throw new ArgumentException(
                $"Blob too short: {blob.Length} < {HeaderLength + TagLength}.", nameof(blob));

        var ephemeralPubKeyBytes = blob[..PubKeyLength].ToArray();
        if (!ECPubKey.TryCreate(ephemeralPubKeyBytes, Context.Instance, out _, out var ephemeralPubKey))
            throw new ArgumentException("Could not parse the ephemeral public key.", nameof(blob));

        var nonce = blob[PubKeyLength..HeaderLength];
        var sealedBytes = blob[HeaderLength..];
        var ciphertext = sealedBytes[..^TagLength];
        var tag = sealedBytes[^TagLength..];

        var symmetricKey = DeriveSymmetricKey(recipientKey, ephemeralPubKey, salt: ephemeralPubKeyBytes);

        var plaintext = new byte[ciphertext.Length];
        using var aead = new AesGcm(symmetricKey, TagLength);
        aead.Decrypt(nonce, ciphertext, tag, plaintext, associatedData: ephemeralPubKeyBytes);
        return plaintext;
    }

    /// <summary>
    /// ECDH against <paramref name="peer"/>, then HKDF-SHA256 to a 32-byte AES key.
    /// </summary>
    private static byte[] DeriveSymmetricKey(ECPrivKey key, ECPubKey peer, byte[] salt)
    {
        // covclaimd's ecdhX takes the x-coordinate of the shared point verbatim.
        // NBitcoin's ECPubKey.GetSharedPubkey applies no hashing either, so the
        // affine x bytes line up with the Go implementation.
        var sharedPoint = peer.GetSharedPubkey(key);
        var sharedSecret = sharedPoint.ToBytes(compressed: true)[1..];

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: sharedSecret,
            outputLength: 32,
            salt: salt,
            info: System.Text.Encoding.ASCII.GetBytes(HkdfInfo));
    }

    private static ECPrivKey CreateEphemeralKey()
    {
        while (true)
        {
            if (ECPrivKey.TryCreate(RandomNumberGenerator.GetBytes(32), Context.Instance, out var key))
                return key;
        }
    }
}

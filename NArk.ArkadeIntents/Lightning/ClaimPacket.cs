using System.Security.Cryptography;
using NBitcoin;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>A sealed preimage and the two values that go with it.</summary>
/// <param name="Packet">
/// What travels as the receive profile's <c>claim_packet</c>: base64 of
/// <c>ephPub(33) || nonce(12) || ciphertext</c>.
/// </param>
/// <param name="Preimage">The secret itself. Never leaves the client.</param>
/// <param name="PaymentHash">
/// <c>sha256(preimage)</c> as hex — what the quote is requested against, and what the solver mints
/// its hold invoice for.
/// </param>
public sealed record SealedClaimPacket(string Packet, byte[] Preimage, string PaymentHash);

/// <summary>
/// Seals a receive swap's preimage to covclaimd, so the claim can be pushed while the client is
/// offline without the solver ever learning the secret.
/// </summary>
/// <remarks>
/// <para>
/// The asymmetry is the point. On a receive swap the solver funds the Arkade side before the
/// Lightning payment it is owed has settled, and it only gets paid when the preimage surfaces in a
/// claim witness. If the solver could open this packet it could settle without ever paying out, so
/// the encryption is to covclaimd's key — not the solver's — and the solver carries it as opaque
/// bytes it cannot read.
/// </para>
/// <para>
/// The scheme is ECIES over secp256k1: an ephemeral key, ECDH, HKDF-SHA256, then AES-256-GCM with
/// the ephemeral public key as additional data. One detail is easy to get wrong and impossible to
/// catch locally — the ECDH shared secret is the 32-byte X coordinate alone, not the compressed
/// point some libraries hand back.
/// </para>
/// </remarks>
public static class ClaimPacket
{
    /// <summary>HKDF <c>info</c>, exactly as covclaimd derives it.</summary>
    private const string HkdfInfo = "covclaimd/preimage/v1";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>
    /// Seal a preimage to covclaimd's public key.
    /// </summary>
    /// <param name="preimage">The 32-byte secret the client chose.</param>
    /// <param name="covclaimdPubKeyHex">
    /// covclaimd's compressed secp256k1 key, from <c>GET /v1/preimage/covclaimd-pubkey</c>. Read it
    /// live — covclaimd generates its own, so a hardcoded value goes stale silently.
    /// </param>
    /// <returns>The packet, the preimage and its payment hash.</returns>
    /// <remarks>
    /// The ECDH shared secret is the 32-byte <b>X coordinate</b>, not the 33-byte compressed point.
    /// Keeping the parity byte still yields a well-formed key on both sides, so nothing local
    /// disagrees — only the remote AEAD tag check fails, and only once a live daemon sees it. The
    /// same trap exists in the JS reference, whose ECDH helper returns the compressed point by
    /// default.
    /// </remarks>
    /// <param name="cipher">
    /// Supplies AES-GCM. Defaults to the platform's, which is right everywhere except a browser —
    /// see <see cref="IAesGcmCipher"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the encryption.</param>
    public static Task<SealedClaimPacket> SealAsync(
        byte[] preimage, string covclaimdPubKeyHex,
        IAesGcmCipher? cipher = null, CancellationToken cancellationToken = default)
    {
        var ephemeral = new Key();
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        return SealAsync(
            preimage, covclaimdPubKeyHex, ephemeral, nonce,
            cipher ?? new AesGcmCipher(), cancellationToken);
    }

    /// <summary>Generate a fresh 32-byte preimage and seal it — how a receive swap starts.</summary>
    /// <param name="covclaimdPubKeyHex">covclaimd's compressed secp256k1 key, hex.</param>
    /// <returns>The packet, the preimage and its payment hash.</returns>
    public static Task<SealedClaimPacket> NewAsync(
        string covclaimdPubKeyHex, IAesGcmCipher? cipher = null, CancellationToken cancellationToken = default) =>
        SealAsync(RandomNumberGenerator.GetBytes(32), covclaimdPubKeyHex, cipher, cancellationToken);

    /// <summary>
    /// The deterministic core, with the ephemeral key and nonce supplied rather than generated.
    /// </summary>
    /// <remarks>
    /// Exposed so the construction can be pinned against the counterparty's own vectors. Callers
    /// outside a test must use <see cref="NewAsync"/>: reusing an ephemeral key or a nonce across
    /// two packets breaks GCM outright.
    /// </remarks>
    internal static async Task<SealedClaimPacket> SealAsync(
        byte[] preimage, string covclaimdPubKeyHex, Key ephemeral, byte[] nonce,
        IAesGcmCipher cipher, CancellationToken cancellationToken = default)
    {
        if (preimage.Length != 32)
        {
            throw new ArgumentException($"preimage must be 32 bytes, got {preimage.Length}", nameof(preimage));
        }
        if (nonce.Length != NonceBytes)
        {
            throw new ArgumentException($"nonce must be {NonceBytes} bytes, got {nonce.Length}", nameof(nonce));
        }

        var ephemeralPub = ephemeral.PubKey.Compress().ToBytes();
        var recipient = new PubKey(Convert.FromHexString(covclaimdPubKeyHex)).Compress();

        // RFC 5903 §9: the shared value is the X coordinate alone. Drop the parity byte.
        var shared = recipient.GetSharedPubkey(ephemeral).Compress().ToBytes()[1..];

        var key = new byte[KeyBytes];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: shared,
            output: key,
            salt: ephemeralPub,
            info: System.Text.Encoding.UTF8.GetBytes(HkdfInfo));

        // The tag trails the ciphertext — the usual ECIES convention, and the only reading under
        // which `ciphertext` is a single trailing field in the documented layout. The cipher
        // returns them already joined, which is also what WebCrypto and Go's Seal hand back.
        var sealedBytes = await cipher.EncryptAsync(
            key, nonce, preimage, associatedData: ephemeralPub, cancellationToken);

        var wire = new byte[ephemeralPub.Length + nonce.Length + sealedBytes.Length];
        ephemeralPub.CopyTo(wire, 0);
        nonce.CopyTo(wire, ephemeralPub.Length);
        sealedBytes.CopyTo(wire, ephemeralPub.Length + nonce.Length);

        return new SealedClaimPacket(
            Convert.ToBase64String(wire),
            preimage,
            Convert.ToHexString(NBitcoin.Crypto.Hashes.SHA256(preimage)).ToLowerInvariant());
    }
}

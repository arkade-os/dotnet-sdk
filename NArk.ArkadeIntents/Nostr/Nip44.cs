using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Nostr;

/// <summary>
/// NIP-44 v2 payload encryption — what seals directed RFQ traffic on a shared relay.
/// </summary>
/// <remarks>
/// <para>
/// The relay operator, and everyone else subscribed to the same kind, sees every event. Only the
/// two parties' shared secret makes a directed payload readable, which is why quotes and requests
/// can ride a public relay at all.
/// </para>
/// <para>
/// Padding is part of the format, not an optimisation: without it the ciphertext length leaks the
/// plaintext length, and on a wire whose messages are this stereotyped that is close to leaking the
/// message type.
/// </para>
/// </remarks>
public static class Nip44
{
    private const byte Version = 2;
    private const int NonceBytes = 32;
    private const int MacBytes = 32;
    private const int MinPlaintext = 1;
    private const int MaxPlaintext = 65535;

    private static readonly byte[] ConversationSalt = "nip44-v2"u8.ToArray();

    /// <summary>
    /// Derive the long-lived conversation key for a pair of parties.
    /// </summary>
    /// <param name="privateKey">Our secret key.</param>
    /// <param name="peerXOnly">The peer's x-only public key (32 bytes).</param>
    /// <returns>The 32-byte conversation key.</returns>
    /// <remarks>
    /// Symmetric by construction — each side derives the same value from its own key and the
    /// other's. Deriving it is the expensive part of the whole scheme (an ECDH), so a client
    /// talking to one solver repeatedly should hold on to the result.
    /// </remarks>
    public static byte[] GetConversationKey(Key privateKey, ReadOnlySpan<byte> peerXOnly)
    {
        if (peerXOnly.Length != 32)
        {
            throw new ArgumentException($"peer key must be 32 bytes x-only, got {peerXOnly.Length}", nameof(peerXOnly));
        }

        // The shared value is the X coordinate alone, and the peer's key is lifted to its even-Y
        // point — the x-only convention BIP340 keys carry, hence the 0x02 prefix.
        var peer = new PubKey([0x02, .. peerXOnly]);
        var shared = peer.GetSharedPubkey(privateKey).Compress().ToBytes()[1..];

        return HKDF.Extract(HashAlgorithmName.SHA256, shared, ConversationSalt);
    }

    /// <summary>Encrypt a payload to a peer.</summary>
    /// <param name="plaintext">The message. Must be 1..65535 bytes as UTF-8.</param>
    /// <param name="conversationKey">From <see cref="GetConversationKey"/>.</param>
    /// <param name="nonce">32 random bytes; a fresh one per message.</param>
    /// <returns>The base64 payload that goes in an event's <c>content</c>.</returns>
    public static string Encrypt(string plaintext, ReadOnlySpan<byte> conversationKey, ReadOnlySpan<byte> nonce)
    {
        var utf8 = Encoding.UTF8.GetBytes(plaintext);
        if (utf8.Length is < MinPlaintext or > MaxPlaintext)
        {
            throw new ArgumentException(
                $"plaintext must be {MinPlaintext}..{MaxPlaintext} bytes, got {utf8.Length}", nameof(plaintext));
        }
        if (nonce.Length != NonceBytes)
        {
            throw new ArgumentException($"nonce must be {NonceBytes} bytes, got {nonce.Length}", nameof(nonce));
        }

        var (chachaKey, chachaNonce, hmacKey) = MessageKeys(conversationKey, nonce);

        var padded = Pad(utf8);
        ChaCha20.XorKeyStream(chachaKey, chachaNonce, padded);

        var mac = HmacWithAad(hmacKey, padded, nonce);

        var payload = new byte[1 + NonceBytes + padded.Length + MacBytes];
        payload[0] = Version;
        nonce.CopyTo(payload.AsSpan(1));
        padded.CopyTo(payload.AsSpan(1 + NonceBytes));
        mac.CopyTo(payload.AsSpan(1 + NonceBytes + padded.Length));

        return Convert.ToBase64String(payload);
    }

    /// <summary>Decrypt a payload from a peer.</summary>
    /// <param name="payload">The base64 <c>content</c>.</param>
    /// <param name="conversationKey">From <see cref="GetConversationKey"/>.</param>
    /// <returns>The plaintext.</returns>
    /// <exception cref="CryptographicException">The payload is malformed, or its MAC does not verify.</exception>
    /// <remarks>
    /// The MAC is checked before anything is unpadded, so a tampered payload never reaches the
    /// length prefix — which is the one field a forger would aim at to make us read out of bounds.
    /// </remarks>
    public static string Decrypt(string payload, ReadOnlySpan<byte> conversationKey)
    {
        if (payload.StartsWith('#'))
        {
            throw new CryptographicException("unsupported NIP-44 version");
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(payload);
        }
        catch (FormatException e)
        {
            throw new CryptographicException("payload is not base64", e);
        }

        if (raw.Length < 1 + NonceBytes + 1 + MacBytes)
        {
            throw new CryptographicException($"payload is too short at {raw.Length} bytes");
        }
        if (raw[0] != Version)
        {
            throw new CryptographicException($"unsupported NIP-44 version {raw[0]}");
        }

        var nonce = raw.AsSpan(1, NonceBytes);
        var ciphertext = raw.AsSpan(1 + NonceBytes, raw.Length - 1 - NonceBytes - MacBytes);
        var mac = raw.AsSpan(raw.Length - MacBytes);

        var (chachaKey, chachaNonce, hmacKey) = MessageKeys(conversationKey, nonce);

        if (!CryptographicOperations.FixedTimeEquals(HmacWithAad(hmacKey, ciphertext, nonce), mac))
        {
            throw new CryptographicException("MAC does not verify — the payload is not ours or was tampered with");
        }

        var padded = ciphertext.ToArray();
        ChaCha20.XorKeyStream(chachaKey, chachaNonce, padded);

        return Unpad(padded);
    }

    /// <summary>Per-message keys, expanded from the conversation key and this message's nonce.</summary>
    private static (byte[] ChachaKey, byte[] ChachaNonce, byte[] HmacKey) MessageKeys(
        ReadOnlySpan<byte> conversationKey, ReadOnlySpan<byte> nonce)
    {
        if (conversationKey.Length != 32)
        {
            throw new ArgumentException(
                $"conversation key must be 32 bytes, got {conversationKey.Length}", nameof(conversationKey));
        }

        var expanded = new byte[76];
        HKDF.Expand(HashAlgorithmName.SHA256, conversationKey.ToArray(), expanded, nonce.ToArray());

        return (expanded[..32], expanded[32..44], expanded[44..76]);
    }

    /// <summary>The MAC covers the nonce as associated data, not just the ciphertext.</summary>
    private static byte[] HmacWithAad(byte[] key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> aad)
    {
        var buffer = new byte[aad.Length + message.Length];
        aad.CopyTo(buffer);
        message.CopyTo(buffer.AsSpan(aad.Length));
        return System.Security.Cryptography.HMACSHA256.HashData(key, buffer);
    }

    /// <summary>
    /// Length-prefix and pad to the next power-of-two-derived bucket, so the ciphertext size says as
    /// little as possible about the plaintext size.
    /// </summary>
    internal static byte[] Pad(byte[] plaintext)
    {
        var padded = new byte[2 + CalcPaddedLength(plaintext.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(padded, (ushort)plaintext.Length);
        plaintext.CopyTo(padded, 2);
        return padded;
    }

    private static string Unpad(byte[] padded)
    {
        if (padded.Length < 2) throw new CryptographicException("padded plaintext is truncated");

        var length = BinaryPrimitives.ReadUInt16BigEndian(padded);
        if (length < MinPlaintext || 2 + length > padded.Length)
        {
            throw new CryptographicException($"declared plaintext length {length} does not fit the payload");
        }
        // The padding is part of what the MAC covered, so a wrong bucket means a crafted payload
        // rather than a benign encoder difference.
        if (padded.Length != 2 + CalcPaddedLength(length))
        {
            throw new CryptographicException("padding does not match the declared length");
        }

        return Encoding.UTF8.GetString(padded, 2, length);
    }

    /// <summary>NIP-44's padding buckets: 32 bytes up to 32, then eighths of the enclosing power of two.</summary>
    internal static int CalcPaddedLength(int unpadded)
    {
        if (unpadded <= 32) return 32;

        var nextPower = 1 << (int)(Math.Floor(Math.Log2(unpadded - 1)) + 1);
        var chunk = nextPower <= 256 ? 32 : nextPower / 8;
        return chunk * ((int)Math.Floor((double)(unpadded - 1) / chunk) + 1);
    }
}

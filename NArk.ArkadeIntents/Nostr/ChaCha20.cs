using System.Buffers.Binary;

namespace NArk.ArkadeIntents.Nostr;

/// <summary>
/// The ChaCha20 stream cipher (RFC 8439 §2.4).
/// </summary>
/// <remarks>
/// Hand-rolled because the platform does not expose the raw stream. .NET ships
/// <c>ChaCha20Poly1305</c>, which is the AEAD construction — it appends and verifies a Poly1305 tag
/// and so produces a different byte layout. NIP-44 v2 uses the bare stream and carries its own
/// HMAC-SHA256 instead, so the AEAD is not a drop-in.
/// </remarks>
internal static class ChaCha20
{
    private const int BlockBytes = 64;

    /// <summary>
    /// XOR <paramref name="data"/> with the keystream. Encryption and decryption are the same
    /// operation.
    /// </summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce.</param>
    /// <param name="data">The bytes to transform, in place.</param>
    /// <param name="counter">Initial block counter. RFC 8439 starts at 1 for AEAD, 0 for the bare stream.</param>
    /// <exception cref="ArgumentException">The key or nonce is the wrong length.</exception>
    public static void XorKeyStream(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> data, uint counter = 0)
    {
        if (key.Length != 32) throw new ArgumentException($"key must be 32 bytes, got {key.Length}", nameof(key));
        if (nonce.Length != 12) throw new ArgumentException($"nonce must be 12 bytes, got {nonce.Length}", nameof(nonce));

        Span<uint> state = stackalloc uint[16];
        // "expand 32-byte k", the constant every ChaCha20 state starts with.
        state[0] = 0x61707865; state[1] = 0x3320646e; state[2] = 0x79622d32; state[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        state[12] = counter;
        for (var i = 0; i < 3; i++) state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(i * 4)..]);

        Span<uint> working = stackalloc uint[16];
        Span<byte> block = stackalloc byte[BlockBytes];

        for (var offset = 0; offset < data.Length; offset += BlockBytes)
        {
            state.CopyTo(working);
            for (var round = 0; round < 10; round++)
            {
                // Column rounds, then diagonal rounds — one "double round" per iteration.
                QuarterRound(working, 0, 4, 8, 12);
                QuarterRound(working, 1, 5, 9, 13);
                QuarterRound(working, 2, 6, 10, 14);
                QuarterRound(working, 3, 7, 11, 15);
                QuarterRound(working, 0, 5, 10, 15);
                QuarterRound(working, 1, 6, 11, 12);
                QuarterRound(working, 2, 7, 8, 13);
                QuarterRound(working, 3, 4, 9, 14);
            }

            for (var i = 0; i < 16; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], working[i] + state[i]);
            }

            var take = Math.Min(BlockBytes, data.Length - offset);
            for (var i = 0; i < take; i++) data[offset + i] ^= block[i];

            state[12]++;
        }
    }

    private static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] = RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d]; s[b] = RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b]; s[d] = RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d]; s[b] = RotateLeft(s[b] ^ s[c], 7);
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}

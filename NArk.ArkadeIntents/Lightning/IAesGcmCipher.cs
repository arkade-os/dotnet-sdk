using System.Security.Cryptography;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>
/// AES-256-GCM, as the one primitive a host may have to supply itself.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in the claim packet — the ECDH, the HKDF — runs anywhere .NET runs. This one
/// does not: under Blazor WebAssembly <see cref="AesGcm"/> is unsupported and throws
/// <see cref="PlatformNotSupportedException"/>, because the browser runtime has no OpenSSL behind
/// it. The browser does have AES-GCM, in WebCrypto, so the gap is reachable — but only by a host
/// that can call JavaScript, which a library cannot.
/// </para>
/// <para>
/// Hence a seam of exactly one operation. The wire format stays defined here and is not the host's
/// business; what the host supplies is a way to compute it.
/// </para>
/// <para>
/// Named for the algorithm rather than for AEAD in general, because the algorithm is not a choice:
/// the packet has to open on the counterparty's side, where it is AES-256-GCM and nothing else. An
/// interface promising to accept any AEAD would invite substituting one, and the resulting packet
/// would be refused by a daemon rather than by a compiler.
/// </para>
/// </remarks>
public interface IAesGcmCipher
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under AES-256-GCM.
    /// </summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce, never reused under the same key.</param>
    /// <param name="plaintext">What to encrypt.</param>
    /// <param name="associatedData">Authenticated but not encrypted.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// Ciphertext with the 16-byte authentication tag appended — the layout WebCrypto returns and
    /// the layout Go's <c>aead.Seal</c> produces, so an implementation that separates them must
    /// concatenate before returning.
    /// </returns>
    Task<byte[]> EncryptAsync(
        byte[] key,
        byte[] nonce,
        byte[] plaintext,
        byte[] associatedData,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The platform implementation, used everywhere except the browser.
/// </summary>
public sealed class AesGcmCipher : IAesGcmCipher
{
    private const int TagBytes = 16;

    /// <inheritdoc />
    public Task<byte[]> EncryptAsync(
        byte[] key,
        byte[] nonce,
        byte[] plaintext,
        byte[] associatedData,
        CancellationToken cancellationToken = default)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var gcm = new AesGcm(key, TagBytes))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        return Task.FromResult<byte[]>([.. ciphertext, .. tag]);
    }
}

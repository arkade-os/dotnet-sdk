using Microsoft.JSInterop;
using NArk.ArkadeIntents.Lightning;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// AES-256-GCM through the browser's WebCrypto, for a runtime that has no other.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.Security.Cryptography.AesGcm"/> throws
/// <see cref="PlatformNotSupportedException"/> under Blazor WebAssembly — there is no OpenSSL
/// behind the managed API there. The browser itself has AES-GCM and always has; it is simply on
/// the other side of the JavaScript boundary, which a library cannot reach and a host can.
/// </para>
/// <para>
/// Without this, the receive corridor cannot run in a browser at all: the claim packet seals the
/// swap preimage with exactly this primitive, and a swap that cannot seal one is a swap the solver
/// refuses before it starts.
/// </para>
/// </remarks>
public sealed class WebCryptoAeadCipher(IJSRuntime js) : IAeadCipher, IAsyncDisposable
{
    private IJSObjectReference? _module;

    /// <inheritdoc />
    public async Task<byte[]> EncryptAsync(
        byte[] key,
        byte[] nonce,
        byte[] plaintext,
        byte[] associatedData,
        CancellationToken cancellationToken = default)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>(
            "import", cancellationToken, "./js/aes-gcm.js");

        var sealedBase64 = await _module.InvokeAsync<string>(
            "encrypt",
            cancellationToken,
            Convert.ToBase64String(key),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(plaintext),
            Convert.ToBase64String(associatedData));

        return Convert.FromBase64String(sealedBase64);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;

        // A page being torn down takes the module with it, so a failed release is the expected
        // ending rather than a fault worth surfacing.
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}

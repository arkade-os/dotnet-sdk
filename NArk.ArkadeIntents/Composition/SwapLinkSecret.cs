using System.Security.Cryptography;

namespace NArk.ArkadeIntents.Composition;

/// <summary>The client-owned secret that links otherwise independent swap quotes.</summary>
/// <remarks>
/// A composed route reuses the payment hash, never the secret itself, across its quote requests.
/// Keep one instance per payment-method attempt: sharing it between alternative checkout rails
/// lets activity on one route settle another.
/// </remarks>
public sealed class SwapLinkSecret
{
    private readonly byte[] _preimage;

    private SwapLinkSecret(byte[] preimage)
    {
        _preimage = preimage;
        PaymentHash = Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
    }

    /// <summary>SHA-256 of the preimage, encoded as 64 lowercase hexadecimal characters.</summary>
    public string PaymentHash { get; }

    /// <summary>Creates an independent random 32-byte route secret.</summary>
    public static SwapLinkSecret Generate() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>Loads a route secret from a protected store.</summary>
    /// <param name="preimage">Exactly 32 bytes. The caller's array is copied.</param>
    public static SwapLinkSecret FromPreimage(ReadOnlySpan<byte> preimage)
    {
        if (preimage.Length != 32)
            throw new ArgumentException("a composed swap preimage must be exactly 32 bytes", nameof(preimage));
        return new SwapLinkSecret(preimage.ToArray());
    }

    /// <summary>Returns a defensive copy for a claim or protected persistence.</summary>
    public byte[] ExportPreimage() => _preimage.ToArray();
}

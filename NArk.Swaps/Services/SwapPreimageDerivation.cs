using System.Security.Cryptography;
using System.Text;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Swaps.Services;

/// <summary>
/// Deterministic swap-preimage derivation, shared by every swap provider.
/// </summary>
/// <remarks>
/// <para>
/// BIP-340 sign+hash gives a deterministic preimage rooted in the wallet's secret material
/// without leaking the key (signatures reveal nothing about the key). The signed message
/// bundles:
/// </para>
/// <list type="bullet">
///   <item><description><b>tag</b> — domain-separates this signature from any other use of the
///   signing key (versioned so a future scheme bump can coexist on recovery);</description></item>
///   <item><description><b>pubkey</b> — the descriptor's x-only public key, scoping the preimage
///   to the swap key. Canonical, unlike <c>descriptor.ToString()</c> (which differs between a
///   signing descriptor and the bare receiver descriptor a restore reconstructs), so create-time
///   and restore-time derive the same value;</description></item>
///   <item><description><b>index</b> — lets a caller derive multiple preimages from one key;
///   always 0 today, but baked into v1 so recovery iteration stays forward-compatible without a
///   scheme bump.</description></item>
/// </list>
/// <para>
/// Same (wallet, pubkey, index) → same signature → same preimage, so a restored wallet that
/// rediscovers an outstanding swap via Boltz <c>/v2/swap/restore</c> can re-derive the preimage
/// and claim the VHTLC. A randomly generated preimage cannot be recovered this way — it only
/// ever existed in local storage, which is precisely what a restore is recovering from.
/// </para>
/// <para>
/// Local signing sources MUST pass <c>aux_rand</c>=zeroes (32 zero bytes) to BIP-340 to produce
/// deterministic signatures. <c>ECPrivKey.SignBIP340(msg)</c> without an explicit auxData draws
/// from the system RNG on each call — pass <c>SignBIP340(msg, new byte[32])</c> instead.
/// Remote-signer transports MUST honour the same convention or the preimage will rotate per call
/// and recovery will silently fail — see <c>IRemoteSignerTransport.SignAsync</c>.
/// </para>
/// <para>
/// The tag is protocol+provider scoped (Arkade brand, Boltz provider), not SDK-scoped, so any
/// Arkade SDK implementing the same scheme produces the same preimage and can recover swaps the
/// .NET SDK created, and vice versa.
/// </para>
/// </remarks>
public static class SwapPreimageDerivation
{
    /// <summary>Domain-separation tag for the signed message. Versioned for future scheme evolution.</summary>
    public const string PreimageTag = "Arkade-Boltz-Preimage-v1";

    private static readonly byte[] PreimageTagBytes = Encoding.UTF8.GetBytes(PreimageTag);

    /// <summary>
    /// Builds the message that gets BIP-340-signed. Cross-SDK format:
    /// <c>PreimageTag ‖ x-only pubkey (32B) ‖ u32le(index)</c>.
    /// </summary>
    public static byte[] BuildMessage(OutputDescriptor descriptor, uint index)
    {
        var keyBytes = OutputDescriptorHelpers.Extract(descriptor).XOnlyPubKey.ToBytes();
        var indexBytes = BitConverter.GetBytes(index);
        if (!BitConverter.IsLittleEndian) Array.Reverse(indexBytes); // canonical u32 LE
        var message = new byte[PreimageTagBytes.Length + keyBytes.Length + indexBytes.Length];
        Buffer.BlockCopy(PreimageTagBytes, 0, message, 0, PreimageTagBytes.Length);
        Buffer.BlockCopy(keyBytes, 0, message, PreimageTagBytes.Length, keyBytes.Length);
        Buffer.BlockCopy(indexBytes, 0, message, PreimageTagBytes.Length + keyBytes.Length, indexBytes.Length);
        return message;
    }

    /// <summary>
    /// Derives the preimage for <paramref name="descriptor"/> at <paramref name="index"/>.
    /// </summary>
    /// <returns>
    /// A deterministic 32-byte preimage, or — when the wallet has no signer (watch-only, no
    /// remote-signer transport) — a random one. A random preimage is <em>not recoverable by
    /// restore</em>; the caller keeps it only in storage.
    /// </returns>
    public static async Task<byte[]> DeriveAsync(
        IWalletProvider walletProvider, string walletId, OutputDescriptor descriptor, uint index,
        CancellationToken cancellationToken = default)
    {
        var signer = await walletProvider.GetSignerAsync(walletId, cancellationToken);
        if (signer is null)
            return RandomUtils.GetBytes(32); // watch-only — no entropy floor to draw from

        var messageHash = new uint256(SHA256.HashData(BuildMessage(descriptor, index)));
        var (_, sig) = await signer.Sign(descriptor, messageHash, cancellationToken);
        return SHA256.HashData(sig.ToBytes());
    }
}

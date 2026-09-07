using System.Security.Cryptography;
using System.Text;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>
/// Deterministic claim preimages for the RFQ corridors: a preimage derived from the swap's own
/// claim key, so the seed re-derives what storage may lose.
/// </summary>
/// <remarks>
/// <para>
/// The scheme is the reference client's, byte for byte: the message is
/// <c>TAG ‖ xonly(32) ‖ u32le(index)</c> (or <c>TAG ‖ xonly(32) ‖ salt(32)</c> when the descriptor
/// repeats across swaps), the preimage is <c>sha256(sign_det(sha256(message)))</c>, and the
/// signature is BIP-340 with <c>aux_rand = 0</c> — the convention every local signing source in
/// this SDK already honours, and the one <c>Arkade-Boltz-Preimage-v1</c> uses on the Boltz
/// corridor. Same (key, salt-or-index) → same signature → same preimage, so a restored wallet
/// re-derives the claim secret from the contract's own descriptor.
/// </para>
/// <para>
/// The tag is protocol-scoped and versioned, and deliberately distinct from the Boltz one: a
/// shared tag would let one wallet key derive one preimage for both corridors. Any Arkade SDK
/// implementing the same scheme produces the same preimage, so artifacts are recoverable across
/// implementations.
/// </para>
/// </remarks>
public static class PreimageProvisioning
{
    /// <summary>Domain separator for the per-artifact derivation — exactly as the reference writes it.</summary>
    public const string PreimageTag = "Arkade-RFQ-Preimage-v1";

    /// <summary>Domain separator for the salted derivation, used when the descriptor repeats.</summary>
    public const string SaltedPreimageTag = "Arkade-Contract-Preimage-Salted-v1";

    /// <summary>
    /// The derivation index. Unsalted derivation is only safe when the key belongs to one swap, so
    /// the index stays pinned; kept a constant for cross-SDK vectors.
    /// </summary>
    public const uint PreimageIndex = 0;

    private static readonly byte[] PreimageTagBytes = Encoding.UTF8.GetBytes(PreimageTag);
    private static readonly byte[] SaltedPreimageTagBytes = Encoding.UTF8.GetBytes(SaltedPreimageTag);

    /// <summary>
    /// <c>TAG ‖ xonly(32) ‖ u32le(index)</c> — the message that gets BIP-340 signed.
    /// </summary>
    /// <remarks>
    /// Anchored on the canonical x-only key rather than the descriptor string: a restore
    /// reconstructs a bare descriptor that serialises differently from the signing descriptor used
    /// at creation, and only the key agrees across both.
    /// </remarks>
    public static byte[] BuildPreimageMessage(byte[] xOnlyPubKey, uint index)
    {
        if (xOnlyPubKey.Length != 32)
            throw new ArgumentException($"x-only pubkey must be 32 bytes, got {xOnlyPubKey.Length}", nameof(xOnlyPubKey));

        var indexBytes = BitConverter.GetBytes(index);
        if (!BitConverter.IsLittleEndian) Array.Reverse(indexBytes); // canonical u32 LE

        var message = new byte[PreimageTagBytes.Length + 32 + 4];
        Buffer.BlockCopy(PreimageTagBytes, 0, message, 0, PreimageTagBytes.Length);
        Buffer.BlockCopy(xOnlyPubKey, 0, message, PreimageTagBytes.Length, 32);
        Buffer.BlockCopy(indexBytes, 0, message, PreimageTagBytes.Length + 32, 4);
        return message;
    }

    /// <summary>
    /// <c>TAG ‖ xonly(32) ‖ salt(32)</c> — the salted message that gets BIP-340 signed.
    /// </summary>
    /// <remarks>
    /// The salt replaces the pinned index as the source of per-swap uniqueness, which is what lets
    /// a key that repeats across swaps still derive a distinct preimage for each. It is public:
    /// knowing it yields nothing without the seed.
    /// </remarks>
    public static byte[] BuildSaltedPreimageMessage(byte[] xOnlyPubKey, byte[] salt)
    {
        if (xOnlyPubKey.Length != 32)
            throw new ArgumentException($"x-only pubkey must be 32 bytes, got {xOnlyPubKey.Length}", nameof(xOnlyPubKey));
        if (salt.Length != 32)
            throw new ArgumentException($"preimage salt must be 32 bytes, got {salt.Length}", nameof(salt));

        var message = new byte[SaltedPreimageTagBytes.Length + 32 + 32];
        Buffer.BlockCopy(SaltedPreimageTagBytes, 0, message, 0, SaltedPreimageTagBytes.Length);
        Buffer.BlockCopy(xOnlyPubKey, 0, message, SaltedPreimageTagBytes.Length, 32);
        Buffer.BlockCopy(salt, 0, message, SaltedPreimageTagBytes.Length + 32, 32);
        return message;
    }

    /// <summary>
    /// True when the descriptor names one swap — an HD child, which carries a derivation path. A
    /// bare <c>tr(pubkey)</c> is the same key every time it is handed out, so anything deriving
    /// per-swap secrets must branch on this, never on the wallet's type.
    /// </summary>
    public static bool IsPerArtifactDescriptor(OutputDescriptor descriptor)
    {
        var text = descriptor.ToString();

        // The question is whether this descriptor yields a DIFFERENT key per contract, because that
        // is what lets the message pin its index and still be unique. Key-origin metadata does not
        // answer it: `tr([fp/86'/1'/0']pubkey)` carries a path and is still one key forever, and
        // reading it as per-artifact would hand every swap on that wallet the same preimage — one
        // counterparty learning its own would learn all of them.
        //
        // So the path that counts is the one AFTER the key, outside any origin brackets. Anything
        // this cannot prove falls to the salted arm, which is unique regardless: mis-salting a
        // descriptor that did not need it costs nothing, while mis-pinning one that did costs every
        // secret the wallet has.
        var afterOrigin = text.LastIndexOf(']');
        var keyPart = afterOrigin >= 0 ? text[(afterOrigin + 1)..] : text;

        return keyPart.Contains('/');
    }

    /// <summary>
    /// <c>sha256(sign_det(sha256(TAG ‖ xonly ‖ index)))</c>, or its salted variant when
    /// <paramref name="salt"/> is given — where the signing key and the message key are the same
    /// descriptor.
    /// </summary>
    /// <param name="signer">The wallet's signer; determinism is the <c>aux_rand = 0</c> convention.</param>
    /// <param name="descriptor">The swap's claim descriptor — the key that signs and the key in the message.</param>
    /// <param name="salt">Per-swap uniqueness for a repeating descriptor; <c>null</c> for an HD child.</param>
    /// <param name="cancellationToken">Cancels the signing round trip.</param>
    /// <returns>The 32-byte preimage.</returns>
    public static async Task<byte[]> DerivePreimageAsync(
        IArkadeWalletSigner signer, OutputDescriptor descriptor, byte[]? salt, CancellationToken cancellationToken = default)
    {
        var xOnly = OutputDescriptorHelpers.Extract(descriptor).XOnlyPubKey.ToBytes();
        var message = salt is null
            ? BuildPreimageMessage(xOnly, PreimageIndex)
            : BuildSaltedPreimageMessage(xOnly, salt);
        var messageHash = new uint256(SHA256.HashData(message));
        var (_, sig) = await signer.Sign(descriptor, messageHash, cancellationToken);
        return SHA256.HashData(sig.ToBytes());
    }
}

using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Crypto;

/// <summary>
/// Computes the BIP-340 tagged hash <c>tagged_hash("ArkScriptHash", script)</c>
/// and the resulting "introspector-tweaked" public key
/// <c>introspector_pubkey + tagged_hash · G</c> that ArkadeScript leaves bind
/// signing authority to a specific script body.
/// </summary>
/// <remarks>
/// <para>
/// The introspector service signs an input only after validating that the
/// script attached to that input hashes to the tweak it computed for the
/// signing key — i.e. the tweak commits the signature path to the exact
/// bytes of the script. Reference:
/// <see href="https://github.com/ArkLabsHQ/introspector"/> — section on tweaked
/// signing keys.
/// </para>
/// <para>
/// Tag string is the literal ASCII <c>"ArkScriptHash"</c>. Both producer and
/// verifier MUST use the same tag — divergence here would silently lock funds
/// to a key the introspector won't sign for.
/// </para>
/// </remarks>
public static class ArkadeScriptHash
{
    /// <summary>The BIP-340 tag bound into every Arkade script hash.</summary>
    public const string TagName = "ArkScriptHash";

    /// <summary>
    /// Computes <c>SHA256(SHA256(tag) || SHA256(tag) || script)</c> for the
    /// <see cref="TagName"/> tag — the 32-byte scalar used to tweak the
    /// introspector's public key.
    /// </summary>
    public static byte[] Compute(ReadOnlySpan<byte> script)
    {
        using var sha = new SHA256();
        sha.InitializeTagged(TagName);
        sha.Write(script);
        return sha.GetHash();
    }

    /// <summary>
    /// Tweaks <paramref name="introspectorPubKey"/> with
    /// <c>tagged_hash("ArkScriptHash", <paramref name="script"/>)</c> and
    /// returns the resulting x-only public key. This is the key the
    /// introspector will sign with for any input whose attached
    /// ArkadeScript matches <paramref name="script"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The tagged hash is not a valid scalar (≥ secp256k1 group order or zero),
    /// or the addition produced the point at infinity. Both cases are
    /// vanishingly improbable for honestly-generated scripts but MUST be
    /// rejected — a silent fall-through would let a caller construct a
    /// commitment to a script the introspector cannot honour.
    /// </exception>
    public static TaprootPubKey Tweak(TaprootPubKey introspectorPubKey, ReadOnlySpan<byte> script)
    {
        var tweakBytes = Compute(script);

        if (!ECPubKey.TryCreate(introspectorPubKey.ToBytes(), Context.Instance, out _, out var ecPub) || ecPub is null)
            throw new ArgumentException("Introspector public key could not be parsed.", nameof(introspectorPubKey));

        if (!ecPub.TryAddTweak(tweakBytes, out var tweaked) || tweaked is null)
            throw new ArgumentException(
                "Failed to apply tagged-hash tweak — script hash is not a valid scalar.", nameof(script));

        var xOnly = tweaked.ToXOnlyPubKey();
        return new TaprootPubKey(xOnly.ToBytes());
    }
}

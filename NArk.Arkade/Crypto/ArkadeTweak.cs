using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Crypto;

/// <summary>
/// Computes the BIP-340 tagged hashes <c>tagged_hash("ArkScriptHash", script)</c>
/// and <c>tagged_hash("ArkWitnessHash", witness)</c>, plus the resulting
/// "emulator-tweaked" public key <c>emulator_pubkey + tagged_hash · G</c> that
/// ArkadeScript leaves bind signing authority to a specific script body.
/// </summary>
/// <remarks>
/// <para>
/// The emulator service signs an input only after validating that the
/// script attached to that input hashes to the tweak it computed for the
/// signing key — i.e. the tweak commits the signature path to the exact
/// bytes of the script. Reference:
/// <see href="https://github.com/arkade-os/emulator"/> — section on tweaked
/// signing keys.
/// </para>
/// <para>
/// Tag strings are the literal ASCII <see cref="ScriptTagName"/> and
/// <see cref="WitnessTagName"/>. Both producer and verifier MUST use the
/// same tag — divergence here would silently lock funds to a key the
/// emulator won't sign for.
/// </para>
/// </remarks>
public static class ArkadeTweak
{
    /// <summary>The BIP-340 tag bound into every Arkade script hash.</summary>
    public const string ScriptTagName = "ArkScriptHash";

    /// <summary>The BIP-340 tag bound into every Arkade witness hash.</summary>
    public const string WitnessTagName = "ArkWitnessHash";

    /// <summary>
    /// Computes <c>SHA256(SHA256(tag) || SHA256(tag) || script)</c> for the
    /// <see cref="ScriptTagName"/> tag — the 32-byte scalar used to tweak the
    /// emulator's public key.
    /// </summary>
    public static byte[] ComputeScriptHash(ReadOnlySpan<byte> script) => ComputeTaggedHash(script, ScriptTagName);

    /// <summary>
    /// Computes <c>SHA256(SHA256(tag) || SHA256(tag) || witness)</c> for the
    /// <see cref="WitnessTagName"/> tag — the hash pushed by
    /// <see cref="Scripts.ArkadeOpcode.OP_INSPECTINPUTARKADEWITNESSHASH"/>.
    /// Returns 32 zero bytes for an empty witness, matching the ts-sdk /
    /// emulator reference.
    /// </summary>
    public static byte[] ComputeWitnessHash(ReadOnlySpan<byte> witness) =>
        witness.IsEmpty ? new byte[32] : ComputeTaggedHash(witness, WitnessTagName);
    
    private static byte[] ComputeTaggedHash(ReadOnlySpan<byte> preimage, string tag)
    {
        using var sha = new SHA256();
        sha.InitializeTagged(tag);
        sha.Write(preimage);
        return sha.GetHash();
    }

    /// <summary>
    /// Tweaks <paramref name="emulatorPubKey"/> with
    /// <c>tagged_hash("ArkScriptHash", <paramref name="script"/>)</c> and
    /// returns the resulting x-only public key. This is the key the
    /// emulator will sign with for any input whose attached
    /// ArkadeScript matches <paramref name="script"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The tagged hash is not a valid scalar (≥ secp256k1 group order or zero),
    /// or the addition produced the point at infinity. Both cases are
    /// vanishingly improbable for honestly-generated scripts but MUST be
    /// rejected — a silent fall-through would let a caller construct a
    /// commitment to a script the emulator cannot honour.
    /// </exception>
    public static TaprootPubKey Tweak(TaprootPubKey emulatorPubKey, ReadOnlySpan<byte> script)
    {
        var tweakBytes = ComputeScriptHash(script);

        if (!ECXOnlyPubKey.TryCreate(emulatorPubKey.ToBytes(), Context.Instance, out var xOnly) || xOnly is null)
            throw new ArgumentException("Emulator public key could not be parsed.", nameof(emulatorPubKey));

        if (!xOnly.TryAddTweak(tweakBytes, out var tweaked) || tweaked is null)
            throw new ArgumentException(
                "Failed to apply tagged-hash tweak — script hash is not a valid scalar.", nameof(script));

        return new TaprootPubKey(tweaked.Q.x.ToBytes());
    }

    /// <summary>
    /// Convenience overload that accepts the emulator's compressed public key
    /// (as returned by <c>GET /v1/info</c>'s <c>signerPubkey</c>) and tweaks its
    /// x-only form. The key's parity is dropped before tweaking, matching the
    /// ts-sdk / emulator reference.
    /// </summary>
    public static TaprootPubKey Tweak(ECPubKey emulatorPubKey, ReadOnlySpan<byte> script)
    {
        ArgumentNullException.ThrowIfNull(emulatorPubKey);
        return Tweak(new TaprootPubKey(emulatorPubKey.ToXOnlyPubKey().ToBytes()), script);
    }
}

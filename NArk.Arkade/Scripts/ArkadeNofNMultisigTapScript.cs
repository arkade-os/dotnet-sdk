using NArk.Abstractions.Scripts;
using NArk.Arkade.Crypto;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Scripts;

/// <summary>
/// An N-of-N multisig tapscript leaf that binds an
/// <see cref="ArkadeScript"/> body to the emulator's signing authority.
/// The leaf's owner set is the base multisig owners <em>plus</em> one tweaked
/// pubkey per emulator — each tweak computed as
/// <c>emulator_pubkey + tagged_hash("ArkScriptHash", arkadeScript) · G</c>
/// (see <see cref="ArkadeScriptHash.Tweak(NBitcoin.TaprootPubKey, System.ReadOnlySpan{byte})"/>).
/// </summary>
/// <remarks>
/// <para>
/// The emulator co-signs only when the script attached to the input
/// hashes to the tweak it computed for its key. By wrapping a base multisig
/// this way we get the cleanest equivalent of the ts-sdk's
/// <c>ArkadeVtxoScript</c> in .NET-idiomatic terms: existing
/// <see cref="NArk.Abstractions.Contracts.ArkContract"/> subclasses can yield
/// instances of this class from <c>GetScriptBuilders()</c> exactly as they
/// already yield <see cref="NofNMultisigTapScript"/> today.
/// </para>
/// <para>
/// Other tapscript variants (CSV multisig, condition multisig, etc.) follow
/// the same augment-the-pubkey-set pattern; add wrappers as needed when those
/// flavours are wanted in production.
/// </para>
/// </remarks>
public sealed class ArkadeNofNMultisigTapScript : ScriptBuilder, IArkadeBoundScriptBuilder
{
    private readonly NofNMultisigTapScript _augmented;

    /// <summary>The ArkadeScript bytecode this leaf is bound to.</summary>
    public byte[] ArkadeScript { get; }

    /// <summary>The emulators whose tweaked keys were appended to the owner set.</summary>
    public IReadOnlyList<TaprootPubKey> EmulatorKeys { get; }

    /// <summary>The post-tweak emulator keys actually present in the multisig owner set.</summary>
    public IReadOnlyList<TaprootPubKey> TweakedEmulatorKeys { get; }

    /// <summary>Owners of the augmented multisig: base owners followed by tweaked emulator keys.</summary>
    public IReadOnlyList<ECXOnlyPubKey> AugmentedOwners => _augmented.Owners;

    public ArkadeNofNMultisigTapScript(
        byte[] arkadeScript,
        IEnumerable<ECXOnlyPubKey> baseOwners,
        IEnumerable<TaprootPubKey> emulatorKeys)
    {
        ArgumentNullException.ThrowIfNull(arkadeScript);
        ArgumentNullException.ThrowIfNull(baseOwners);
        ArgumentNullException.ThrowIfNull(emulatorKeys);
        if (arkadeScript.Length == 0)
            throw new ArgumentException("ArkadeScript bytecode cannot be empty.", nameof(arkadeScript));

        ArkadeScript = arkadeScript;
        EmulatorKeys = emulatorKeys.ToArray();
        if (EmulatorKeys.Count == 0)
            throw new ArgumentException("At least one emulator key is required.", nameof(emulatorKeys));

        var tweaked = EmulatorKeys
            .Select(k => ArkadeScriptHash.Tweak(k, ArkadeScript))
            .ToArray();
        TweakedEmulatorKeys = tweaked;

        var owners = baseOwners.ToList();
        foreach (var t in tweaked)
            owners.Add(ECXOnlyPubKey.Create(t.ToBytes()));

        _augmented = new NofNMultisigTapScript(owners.ToArray());
    }

    /// <inheritdoc />
    public override IEnumerable<Op> BuildScript() => _augmented.BuildScript();
}

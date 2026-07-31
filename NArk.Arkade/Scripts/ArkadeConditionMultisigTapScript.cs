using NArk.Abstractions.Scripts;
using NArk.Arkade.Crypto;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Scripts;

/// <summary>
/// A tapscript leaf gating an N-of-N multisig behind a script condition, with
/// the emulator's signing authority bound to an <see cref="ArkadeScript"/> body.
/// The Arkade equivalent of arkd's <c>ConditionMultisigClosure</c>.
/// </summary>
/// <remarks>
/// <para>
/// Emits <c>&lt;condition&gt; OP_VERIFY &lt;multisig&gt;</c>, where the owner set is the
/// base owners followed by one tweaked pubkey per emulator (see
/// <see cref="ArkadeTweak.Tweak(TaprootPubKey, ReadOnlySpan{byte})"/>). Every
/// owner but the last is checked with <c>OP_CHECKSIGVERIFY</c>; the last uses
/// <c>OP_CHECKSIG</c>, matching arkd's <c>MultisigClosure</c> wire format.
/// </para>
/// <para>
/// This is the shape <c>covclaimd</c> looks for when deciding whether it can
/// claim an output non-interactively: a leaf whose condition is
/// <c>HASH160(preimage) EQUAL</c> and whose owners are exactly
/// <c>(serverKey, emulatorTweakedKey(arkadeScript))</c>. It differs from
/// <see cref="ArkadeNofNMultisigTapScript"/> in two ways that matter here — it
/// prepends a condition, and it terminates the owner set itself rather than
/// relying on a path wrapper such as
/// <see cref="NArk.Core.Scripts.CollaborativePathArkTapScript"/> to supply the
/// final <c>OP_CHECKSIG</c>. Ordering is load-bearing: the emulator key must be
/// last, so it cannot be produced by wrapping a base multisig.
/// </para>
/// </remarks>
public sealed class ArkadeConditionMultisigTapScript : ScriptBuilder, IArkadeBoundScriptBuilder
{
    private readonly ScriptBuilder _condition;
    private readonly ECXOnlyPubKey[] _owners;

    /// <inheritdoc />
    public byte[] ArkadeScript { get; }

    /// <inheritdoc />
    public WitScript? ArkadeScriptWitness { get; }

    /// <inheritdoc />
    public IReadOnlyList<TaprootPubKey> EmulatorKeys { get; }

    /// <summary>The post-tweak emulator keys actually present in the multisig owner set.</summary>
    public IReadOnlyList<TaprootPubKey> TweakedEmulatorKeys { get; }

    /// <summary>Owners of the multisig: base owners followed by tweaked emulator keys.</summary>
    public IReadOnlyList<ECXOnlyPubKey> Owners => _owners;

    /// <param name="arkadeScript">
    /// The ArkadeScript bytecode the emulator executes for this leaf, and whose
    /// tagged hash tweaks each emulator key.
    /// </param>
    /// <param name="condition">
    /// The gating condition, emitted verbatim before <c>OP_VERIFY</c> — e.g. a
    /// <see cref="NArk.Core.Scripts.HashLockTapScript"/> for a preimage gate. Must
    /// leave exactly one truthy value on the stack.
    /// </param>
    /// <param name="baseOwners">Untweaked owners (typically just the Arkade server key).</param>
    /// <param name="emulatorKeys">Pre-tweak emulator pubkeys; appended, in order, after the base owners.</param>
    /// <param name="arkadeScriptWitness">Witness the emulator pushes before executing the script, if any.</param>
    public ArkadeConditionMultisigTapScript(
        byte[] arkadeScript,
        ScriptBuilder condition,
        IEnumerable<ECXOnlyPubKey> baseOwners,
        IEnumerable<TaprootPubKey> emulatorKeys,
        WitScript? arkadeScriptWitness = null)
    {
        ArgumentNullException.ThrowIfNull(arkadeScript);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(baseOwners);
        ArgumentNullException.ThrowIfNull(emulatorKeys);
        if (arkadeScript.Length == 0)
            throw new ArgumentException("ArkadeScript bytecode cannot be empty.", nameof(arkadeScript));

        ArkadeScript = arkadeScript;
        ArkadeScriptWitness = arkadeScriptWitness;
        _condition = condition;

        EmulatorKeys = emulatorKeys.ToArray();
        if (EmulatorKeys.Count == 0)
            throw new ArgumentException("At least one emulator key is required.", nameof(emulatorKeys));

        TweakedEmulatorKeys = EmulatorKeys.Select(k => ArkadeTweak.Tweak(k, ArkadeScript)).ToArray();

        _owners =
        [
            .. baseOwners,
            .. TweakedEmulatorKeys.Select(t => ECXOnlyPubKey.Create(t.ToBytes())),
        ];
    }

    /// <inheritdoc />
    public override IEnumerable<Op> BuildScript()
    {
        foreach (var op in _condition.BuildScript())
            yield return op;

        yield return OpcodeType.OP_VERIFY;

        for (var i = 0; i < _owners.Length; i++)
        {
            yield return Op.GetPushOp(_owners[i].ToBytes());
            yield return i == _owners.Length - 1
                ? OpcodeType.OP_CHECKSIG
                : OpcodeType.OP_CHECKSIGVERIFY;
        }
    }
}

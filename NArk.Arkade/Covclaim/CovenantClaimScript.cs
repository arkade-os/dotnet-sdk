using NArk.Arkade.Scripts;
using NBitcoin;

namespace NArk.Arkade.Covclaim;

/// <summary>
/// Builds and validates the <c>EnforcePayTo</c> ArkadeScript that binds a
/// covenant claim to a single destination — the only ArkadeScript shape
/// <c>covclaimd</c> accepts in v1.
/// </summary>
/// <remarks>
/// <para>
/// The script is what makes non-interactive claiming safe. Its bytes are
/// tagged-hashed into the emulator's signing key (see
/// <see cref="Crypto.ArkadeTweak.Tweak(TaprootPubKey, ReadOnlySpan{byte})"/>),
/// so the emulator only co-signs a transaction that pays the exact destination
/// committed to here. A claim bot can therefore hold the preimage without being
/// able to steal: a transaction paying anywhere else fails the script, and no
/// signature for it exists.
/// </para>
/// <para>
/// Executed semantics — for the input currently being spent, assert that the
/// output at the same index is a P2TR paying <c>receiver</c>, and that its value
/// is at least the input's value:
/// </para>
/// <code>
/// OP_PUSHCURRENTINPUTINDEX OP_DUP OP_INSPECTOUTPUTSCRIPTPUBKEY
/// OP_1 OP_EQUALVERIFY            // witness version must be 1
/// &lt;32-byte witness program&gt; OP_EQUALVERIFY
/// OP_INSPECTOUTPUTVALUE
/// OP_PUSHCURRENTINPUTINDEX OP_INSPECTINPUTVALUE
/// OP_GREATERTHANOREQUAL
/// </code>
/// <para>
/// Byte-compatible with covclaimd's <c>preimage.EnforcePayTo</c>
/// (<c>pkg/preimage/contract.go</c>); the two MUST agree exactly or the emulator
/// computes a different tweak and the claim can never be signed.
/// </para>
/// </remarks>
public static class CovenantClaimScript
{
    /// <summary>Length of a P2TR scriptPubKey: <c>OP_1 OP_DATA_32 &lt;32 bytes&gt;</c>.</summary>
    private const int P2trScriptLength = 34;

    /// <summary>The single-byte opcode for a minimal 32-byte data push.</summary>
    private const byte OpData32 = 0x20;

    /// <summary>
    /// Builds the <c>EnforcePayTo</c> ArkadeScript committing the claim to
    /// <paramref name="receiverScriptPubKey"/>.
    /// </summary>
    /// <param name="receiverScriptPubKey">
    /// The claim destination. Must be a P2TR scriptPubKey — the covenant inspects
    /// a witness-v1 program, so no other output type can be committed to.
    /// </param>
    /// <returns>The ArkadeScript bytecode.</returns>
    /// <exception cref="ArgumentException"><paramref name="receiverScriptPubKey"/> is not P2TR.</exception>
    public static byte[] EnforcePayTo(Script receiverScriptPubKey)
    {
        ArgumentNullException.ThrowIfNull(receiverScriptPubKey);
        var witnessProgram = ExtractP2trWitnessProgram(
            receiverScriptPubKey.ToBytes(), nameof(receiverScriptPubKey));

        return ArkadeScript.Encode(
        [
            Arkade(ArkadeOpcode.OP_PUSHCURRENTINPUTINDEX),
            Tap(OpcodeType.OP_DUP),
            Arkade(ArkadeOpcode.OP_INSPECTOUTPUTSCRIPTPUBKEY),
            Tap(OpcodeType.OP_1),
            Tap(OpcodeType.OP_EQUALVERIFY),
            Op.GetPushOp(witnessProgram),
            Tap(OpcodeType.OP_EQUALVERIFY),
            Arkade(ArkadeOpcode.OP_INSPECTOUTPUTVALUE),
            Arkade(ArkadeOpcode.OP_PUSHCURRENTINPUTINDEX),
            Arkade(ArkadeOpcode.OP_INSPECTINPUTVALUE),
            Tap(OpcodeType.OP_GREATERTHANOREQUAL),
        ]);
    }

    /// <summary>
    /// Validates that <paramref name="scriptPubKey"/> is a P2TR script and returns
    /// its 32-byte witness program.
    /// </summary>
    /// <param name="scriptPubKey">Candidate scriptPubKey bytes.</param>
    /// <param name="paramName">
    /// Name of the caller's parameter, so a rejection points at the argument the caller
    /// actually passed rather than at this private helper's local.
    /// </param>
    private static byte[] ExtractP2trWitnessProgram(byte[] scriptPubKey, string paramName)
    {
        if (scriptPubKey.Length != P2trScriptLength)
            throw new ArgumentException(
                $"Expected a {P2trScriptLength}-byte P2TR scriptPubKey, got {scriptPubKey.Length}.",
                paramName);

        if (scriptPubKey[0] != (byte)OpcodeType.OP_1 || scriptPubKey[1] != OpData32)
            throw new ArgumentException(
                $"Not a P2TR scriptPubKey: prefix {scriptPubKey[0]:x2} {scriptPubKey[1]:x2}.",
                paramName);

        return scriptPubKey[2..];
    }

    // NBitcoin's implicit OpcodeType -> Op conversion treats any byte outside the
    // push range as an opaque single-byte opcode, which is exactly what the Arkade
    // extension opcodes need. These two shims keep the script body above readable.
    private static Op Arkade(ArkadeOpcode opcode) => (OpcodeType)(byte)opcode;

    private static Op Tap(OpcodeType opcode) => opcode;
}

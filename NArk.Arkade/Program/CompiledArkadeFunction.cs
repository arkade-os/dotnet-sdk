using NArk.Abstractions.Scripts;
using NArk.Arkade.Program.Models;
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Program;

/// <summary>
/// One fully-resolved spending path of a compiled <see cref="ArkadeProgram"/> — its
/// definition plus the committed leaf script bytes. Mirrors the ts-sdk's
/// <c>CompiledProgramFunction</c>, minus the taproot-tree/control-block parts (those
/// are derived once all leaves are known, outside the compiler itself).
/// </summary>
public sealed class CompiledArkadeFunction
{
    /// <summary>The function's name, as declared in <see cref="ArkadeProgram.Functions"/>.</summary>
    public required string Name { get; init; }

    /// <summary>The source definition this was compiled from.</summary>
    public required ArkadeFunction Definition { get; init; }

    /// <summary>The committed tapscript leaf body — includes the tweaked co-signer key when <see cref="ArkadeScriptBytes"/> is set.</summary>
    public required byte[] LeafScript { get; init; }

    /// <summary>Resolved covenant (ArkadeScript) bytes; <c>null</c> for pure-tapscript paths.</summary>
    public byte[]? ArkadeScriptBytes { get; init; }

    /// <summary>The pre-tweak emulator key used for this leaf; <c>null</c> for pure-tapscript paths.</summary>
    public TaprootPubKey? EmulatorKey { get; init; }

    /// <summary>
    /// The resolved declared signer keys (x-only), in <see cref="TapscriptSegment.Signers"/> order —
    /// excludes the covenant co-signer key appended for <see cref="ArkadeScriptBytes"/> paths. Mirrors
    /// the ts-sdk's <c>signerKeys</c>; used to detect which paths the wallet's own key can sign.
    /// </summary>
    public required IReadOnlyList<ECXOnlyPubKey> SignerKeys { get; init; }

    /// <summary>
    /// Wraps this leaf as a <see cref="ScriptBuilder"/>, ready to hand to
    /// <see cref="NArk.Abstractions.Contracts.ArkContract.GetScriptBuilders"/>. Covenant
    /// leaves come back as an <see cref="IArkadeBoundScriptBuilder"/> so the existing
    /// emulator co-signing pipeline (<see cref="NArk.Arkade.Emulator.ArkadePsbtExtensions"/>)
    /// picks them up with no further wiring.
    /// </summary>
    public ScriptBuilder ToScriptBuilder(WitScript? arkadeScriptWitness = null)
    {
        var ops = ArkadeScript.Decode(LeafScript);
        return ArkadeScriptBytes is { } script && EmulatorKey is { } key
            ? new ArkadeProgramFunctionScriptBuilder(ops, script, key, arkadeScriptWitness)
            : new GenericTapScript(ops);
    }
}

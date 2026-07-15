using NArk.Arkade.Program.Models;
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;
using NBitcoin;

namespace NArk.Arkade.Program;

/// <summary>
/// A compiled <see cref="ArkadeProgram"/> covenant leaf, wrapped so it is detected as
/// arkade-bound by the existing emulator co-signing pipeline — same role as
/// <see cref="ArkadeNofNMultisigTapScript"/>, but for arbitrary compiled leaves instead
/// of a hardcoded N-of-N.
/// </summary>
public sealed class ArkadeProgramFunctionScriptBuilder : GenericTapScript, IArkadeBoundScriptBuilder
{
    /// <inheritdoc />
    public byte[] ArkadeScript { get; }

    /// <inheritdoc />
    public IReadOnlyList<TaprootPubKey> EmulatorKeys { get; }

    public ArkadeProgramFunctionScriptBuilder(IEnumerable<Op> ops, byte[] arkadeScript, TaprootPubKey emulatorKey)
        : base(ops)
    {
        ArkadeScript = arkadeScript;
        EmulatorKeys = [emulatorKey];
    }
}

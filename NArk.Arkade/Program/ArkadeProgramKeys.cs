using NBitcoin.Secp256k1;

namespace NArk.Arkade.Program;

/// <summary>
/// The signer keys an <see cref="ArkadeProgram"/> is compiled against. Mirrors the
/// ts-sdk's <c>ProgramKeys</c>.
/// </summary>
public sealed class ArkadeProgramKeys
{
    /// <summary>The Arkade Service signer key (x-only), resolved from server info.</summary>
    public required ECXOnlyPubKey ServerKey { get; init; }

    /// <summary>The wallet's x-only key — required only when a function names the <c>"user"</c> signer.</summary>
    public ECXOnlyPubKey? UserKey { get; init; }

    /// <summary>The co-signer (emulator) key — required only for functions with a covenant segment.</summary>
    public ECXOnlyPubKey? EmulatorKey { get; init; }
}

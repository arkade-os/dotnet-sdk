using NBitcoin.Secp256k1;

namespace NArk.Arkade.Program.Models;

/// <summary>
/// The signer keys an <see cref="ArkadeProgram"/> is compiled against. Mirrors the
/// ts-sdk's <c>ProgramKeys</c>.
/// </summary>
public sealed class ArkadeProgramKeys
{
    /// <summary>
    /// The Arkade Service signer key (x-only). Contract-layer metadata — used for address
    /// derivation and to default the <c>$server</c> param; not consulted during <c>$param</c>
    /// resolution in the compiler (signers resolve purely from the bound args).
    /// </summary>
    public required ECXOnlyPubKey ServerKey { get; init; }

    /// <summary>The wallet's x-only key — used to default the <c>$user</c> param when the program declares it.</summary>
    public ECXOnlyPubKey? UserKey { get; init; }

    /// <summary>The co-signer (emulator) key — required only for functions with a covenant segment.</summary>
    public ECXOnlyPubKey? EmulatorKey { get; init; }
}

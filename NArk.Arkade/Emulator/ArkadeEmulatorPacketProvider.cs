using NArk.Abstractions;
using NArk.Core.Assets;

namespace NArk.Arkade.Emulator;

/// <summary>
/// <see cref="ISpendExtensionPacketProvider"/> that contributes the Arkade
/// <see cref="EmulatorPacket"/> to a spend's Extension OP_RETURN, so covenant
/// (arkade-bound) inputs carry the script bytes + witness the emulator needs to
/// co-sign. Stateless and cheap: returns nothing when the spend has no
/// arkade-bound input, so it can be registered unconditionally.
/// </summary>
/// <remarks>
/// This wires the OP_RETURN half of covenant spending into the generic offchain
/// path; the emulator co-signing round-trip itself is driven separately
/// (<see cref="ArkadeBatchSessionExtension"/> for batches / the emulator submit
/// on the ark-tx path).
/// </remarks>
public sealed class ArkadeEmulatorPacketProvider : ISpendExtensionPacketProvider
{
    /// <inheritdoc />
    public IReadOnlyList<IExtensionPacket> BuildPackets(IReadOnlyList<ArkCoin> coinsByVin)
        => ArkadePsbtExtensions.BuildEmulatorPackets(coinsByVin);
}

using NArk.Abstractions;

namespace NArk.Core.Assets;

/// <summary>
/// Plug-point that lets a higher layer (e.g. <c>NArk.Arkade</c>) contribute extra
/// <see cref="IExtensionPacket"/> records to the single Extension OP_RETURN a spend
/// carries, without <c>NArk.Core</c> taking a dependency on that layer. Mirrors the
/// role <c>IBatchSessionExtension</c> plays for batch flows, but for the offchain
/// spend path.
/// </summary>
/// <remarks>
/// Providers are invoked at transaction-assembly time with the spend's inputs in
/// transaction-input order (index <c>i</c> = <c>vin = i</c>), so packets that bind
/// data to specific inputs (like the Arkade emulator packet) can emit correct
/// <c>vin</c> values. A provider with nothing to contribute for a given spend MUST
/// return an empty list, in which case it adds no output.
/// </remarks>
public interface ISpendExtensionPacketProvider
{
    /// <summary>
    /// Build any extension packets this provider wants attached to the spend's
    /// Extension OP_RETURN. Return an empty list to contribute nothing.
    /// </summary>
    /// <param name="coinsByVin">Spend inputs in transaction-input order.</param>
    IReadOnlyList<IExtensionPacket> BuildPackets(IReadOnlyList<ArkCoin> coinsByVin);
}

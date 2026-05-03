namespace NArk.Arkade.Introspector;

/// <summary>
/// One entry in an <see cref="IntrospectorPacket"/> — binds a transaction
/// input to the ArkadeScript bytecode the introspector should execute and
/// the witness data that script consumes.
/// </summary>
/// <param name="Vin">The 16-bit transaction input index (little-endian on the wire).</param>
/// <param name="Script">ArkadeScript bytecode to evaluate against this input. Must be non-empty.</param>
/// <param name="Witness">
/// Witness data the script reads — the wire format treats this as opaque bytes.
/// The bytes typically encode a list of pushes via
/// <see cref="IntrospectorPacket.EncodePushList"/> /
/// <see cref="IntrospectorPacket.DecodePushList"/>; consumers that don't need
/// that level of structure can hand it to the introspector untouched.
/// </param>
public sealed record IntrospectorEntry(
    ushort Vin,
    byte[] Script,
    byte[] Witness);

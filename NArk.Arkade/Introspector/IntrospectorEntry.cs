namespace NArk.Arkade.Introspector;

/// <summary>
/// One entry in an <see cref="IntrospectorPacket"/> — binds a transaction
/// input to the ArkadeScript bytecode the introspector should execute and
/// the witness stack that script consumes.
/// </summary>
/// <param name="Vin">The 16-bit transaction input index (little-endian on the wire).</param>
/// <param name="Script">ArkadeScript bytecode to evaluate against this input. Must be non-empty.</param>
/// <param name="Witness">
/// Witness stack — an ordered list of pushes the script reads. On the wire
/// this is serialized via Bitcoin's standard <c>WriteTxWitness</c> shape
/// (<c>compactSize(num_items) + per-item compactSize(len) + bytes</c>),
/// matching the Go reference's <c>wire.TxWitness</c>.
/// </param>
public sealed record IntrospectorEntry(
    ushort Vin,
    byte[] Script,
    IReadOnlyList<byte[]> Witness);

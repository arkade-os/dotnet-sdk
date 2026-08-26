namespace NArk.ArkadeIntents;

/// <summary>Settings shared by both Arkade intent corridors.</summary>
public sealed class ArkadeIntentsOptions
{
    /// <summary>
    /// Co-sign covenants with this key instead of the network's pinned one. 33-byte compressed hex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Left unset — the normal case — the co-signer comes from
    /// <see cref="NArk.Arkade.Emulator.EmulatorPubKeys.DefaultFor"/>, which is a property of the
    /// network rather than of whatever host answers. Set, every covenant this SDK builds can be
    /// completed by whoever holds the supplied key and by nobody else, so it is a statement that you
    /// trust that operator in place of the network's.
    /// </para>
    /// <para>
    /// It exists mainly for a key rotation this SDK has not shipped a constant for yet, and
    /// secondarily for a self-hosted emulator or a local stack. See
    /// <see cref="NArk.Arkade.Emulator.EmulatorPubKeys.Resolve"/>.
    /// </para>
    /// </remarks>
    public string? EmulatorPubkeyOverride { get; set; }
}

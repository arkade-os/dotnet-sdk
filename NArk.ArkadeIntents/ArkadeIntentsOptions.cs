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

    /// <summary>
    /// Refuse a receive quote that bills the payer more than this, in sats. Unset means no ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A receive request pins one leg and leaves the other to the solver. Pinning what the payer is
    /// billed fixes it exactly and needs no ceiling; pinning what lands on Arkade leaves the payer's
    /// side as the solver's free variable, and this is the only thing bounding it.
    /// </para>
    /// <para>
    /// Nothing is at risk either way — a quote is refused before an invoice reaches anyone, and the
    /// amount that lands is checked separately. What a ceiling prevents is handing a customer an
    /// invoice for more than the order they approved, which their wallet may refuse outright.
    /// </para>
    /// </remarks>
    public long? MaxPayAmountSats { get; set; }
}

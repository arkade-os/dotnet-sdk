namespace NArk.ArkadeIntents.SolverRegistry;

/// <summary>
/// A solver's source market card (Arkade Market Discovery Protocol v0), stored in a registry repo
/// at <c>solvers/&lt;network&gt;/&lt;name&gt;.json</c> or supplied locally. The reducer indexes these
/// into a <see cref="GetSolverRegistryResponse"/>; clients may also merge local cards directly.
/// </summary>
public sealed class SolverCard
{
    /// <summary>Discovery protocol version.</summary>
    public int Version { get; init; }

    /// <summary>Solver name (used as the market's <see cref="IndexedMarket.Solver"/> tag).</summary>
    public required string Name { get; init; }

    /// <summary>Optional discovery x-only pubkey (64-hex) — the key RFQ traffic is addressed to.</summary>
    public string? DiscoveryPubkey { get; init; }

    /// <summary>
    /// The emulator key this solver's covenants are co-signed by (x-only, 64-hex).
    /// </summary>
    /// <remarks>
    /// A fact about the deployment rather than about any one swap, so it is published once here
    /// instead of repeated on every quote — and here it carries provenance, tied to a signed and
    /// reviewable identity, which a field on the wire would not.
    /// </remarks>
    public string? EmulatorPubkey { get; init; }

    /// <summary>
    /// Where to reach this solver, keyed by protocol.
    /// </summary>
    /// <remarks>
    /// The protocol itself carries no URLs — parties are addressed by pubkey — so without this a
    /// <see cref="DiscoveryPubkey"/> names a solver nothing can actually dial.
    /// </remarks>
    public SolverTransports? Transports { get; init; }

    /// <summary>Optional schnorr signature over the card (128-hex).</summary>
    public string? Sig { get; init; }

    /// <summary>The markets this solver advertises.</summary>
    public List<SolverMarket> Markets { get; init; } = [];
}

/// <summary>Rendezvous data, keyed by protocol so a second transport is an added key.</summary>
public sealed class SolverTransports
{
    /// <summary>Nostr rendezvous. The only protocol v0 admits, and required within the map.</summary>
    public NostrTransport? Nostr { get; init; }
}

/// <summary>Where a solver listens for Nostr traffic.</summary>
public sealed class NostrTransport
{
    /// <summary>The relays it connects out to. Both sides dial out; neither listens.</summary>
    public List<string> Relays { get; init; } = [];
}

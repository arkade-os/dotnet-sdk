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

    /// <summary>Optional discovery x-only pubkey (64-hex).</summary>
    public string? DiscoveryPubkey { get; init; }

    /// <summary>Optional schnorr signature over the card (128-hex).</summary>
    public string? Sig { get; init; }

    /// <summary>The markets this solver advertises.</summary>
    public List<SolverMarket> Markets { get; init; } = [];
}

using NArk.ArkadeIntents.Services;

namespace NArk.ArkadeIntents.SolverRegistry;

/// <summary>
/// A per-network index (the reducer/CI output published as <c>&lt;network&gt;.json</c>) in the Arkade
/// Market Discovery Protocol v0. Markets are pre-sorted ascending by <c>fee_bps</c> within each
/// id-pair group.
/// </summary>
public sealed class GetSolverRegistryResponse
{
    /// <summary>Discovery protocol version (must be <see cref="SolverDiscoveryService.SupportedVersion"/>).</summary>
    public int Version { get; init; }

    /// <summary>The network this index covers (<c>bitcoin</c>, <c>signet</c> or <c>mutinynet</c>).</summary>
    public required string Network { get; init; }

    /// <summary>Unix timestamp (seconds) the index was generated; used for staleness warnings.</summary>
    public ulong GeneratedAt { get; init; }

    /// <summary>The git commit the index was reduced from.</summary>
    public string? Commit { get; init; }

    /// <summary>The indexed markets across all solvers on this network.</summary>
    public List<IndexedMarket> Markets { get; init; } = [];
}

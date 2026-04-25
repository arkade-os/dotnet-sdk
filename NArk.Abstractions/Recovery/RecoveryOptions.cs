namespace NArk.Abstractions.Recovery;

/// <summary>
/// Configuration for an HD wallet recovery scan.
/// </summary>
/// <param name="GapLimit">
/// Number of consecutive unused derivation indices that signals the end of
/// usage and stops the scan. BIP44 recommends 20; tune higher for wallets
/// known to have generated many addresses ahead of use.
/// </param>
/// <param name="MaxIndex">
/// Hard upper bound on the highest index probed. Acts as a safety stop in
/// case the gap is never reached on a pathological provider.
/// </param>
/// <param name="StartIndex">
/// First derivation index to probe. Default 0 (full scan from the start);
/// callers can resume a partial scan by passing the highest known index.
/// </param>
public record RecoveryOptions(
    int GapLimit = 20,
    int MaxIndex = 10_000,
    int StartIndex = 0)
{
    /// <summary>Default options (gap=20, max=10000, start=0).</summary>
    public static RecoveryOptions Default { get; } = new();
}

/// <summary>
/// Summary of an HD wallet recovery scan.
/// </summary>
/// <param name="HighestUsedIndex">
/// Highest derivation index that any provider reported as used; <c>-1</c> if
/// no usage was detected at all.
/// </param>
/// <param name="ScannedCount">Total number of indices probed.</param>
/// <param name="DiscoveredContracts">
/// Contracts that were reconstructed and persisted during the scan.
/// </param>
/// <param name="ProviderHits">
/// Per-provider counts of indices each one reported as used. Useful for
/// logging and tests.
/// </param>
public record RecoveryReport(
    int HighestUsedIndex,
    int ScannedCount,
    IReadOnlyList<DiscoveredContract> DiscoveredContracts,
    IReadOnlyDictionary<string, int> ProviderHits);

/// <summary>
/// A single contract that recovery reconstructed, with the index it came from
/// and the provider that detected it.
/// </summary>
public record DiscoveredContract(
    int Index,
    string ProviderName,
    NArk.Abstractions.Contracts.ArkContract Contract);

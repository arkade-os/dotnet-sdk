using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NBitcoin.Scripting;

namespace NArk.Abstractions.Recovery;

/// <summary>
/// Probes a single derivation index of an HD wallet to determine whether any
/// contract derivable from that index has ever been used. Each implementation
/// answers from a different source of truth (arkd indexer, on-chain UTXO set,
/// boltz swap history, etc.) and they are aggregated by
/// <c>HdWalletRecoveryService</c> using a logical OR — if any provider sees
/// usage at an index, the index counts as used and the gap counter resets.
/// </summary>
/// <remarks>
/// <para>
/// Providers are stateless and may be queried concurrently for different
/// indices (and concurrently with each other for the same index).
/// </para>
/// <para>
/// As a rule, providers SHOULD return reconstructed contracts via
/// <see cref="DiscoveryResult.Contracts"/> and let the orchestrator persist
/// them once recovery completes — this keeps the orchestrator the single
/// writer and lets it dedupe across providers.
/// </para>
/// <para>
/// The deliberate exception is when a provider already has a richer import
/// path of its own that produces metadata the orchestrator cannot reconstruct.
/// <c>BoltzSwapDiscoveryProvider</c> is the canonical example: it delegates to
/// <c>SwapsManagementService.RestoreSwaps</c>, which writes both the contract
/// (with <c>Source=swap:&lt;id&gt;</c> metadata) and the corresponding
/// <c>SwapData</c> row in one transaction. Returning those contracts back
/// through the orchestrator would either lose the swap-id linkage or require
/// duplicating the same write. Such providers should set
/// <see cref="DiscoveryResult.Used"/> to <c>true</c> and leave
/// <see cref="DiscoveryResult.Contracts"/> empty so the orchestrator does not
/// re-import what they already persisted.
/// </para>
/// </remarks>
public interface IContractDiscoveryProvider
{
    /// <summary>
    /// Short identifier used in log lines and the recovery report
    /// (e.g. <c>"indexer"</c>, <c>"boarding"</c>, <c>"boltz"</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Probe whether any contract derivable from <paramref name="userDescriptor"/>
    /// at <paramref name="index"/> has ever been used.
    /// </summary>
    /// <param name="wallet">The HD wallet being recovered.</param>
    /// <param name="userDescriptor">
    /// The output descriptor at the given index — concrete (no remaining
    /// <c>/*</c>), already derived from <c>wallet.AccountDescriptor</c>.
    /// </param>
    /// <param name="index">The derivation index being probed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DiscoveryResult"/> describing whether the index was used and,
    /// if so, the reconstructed contracts to persist.
    /// </returns>
    Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of querying a single <see cref="IContractDiscoveryProvider"/> for one index.
/// </summary>
/// <param name="Used">
/// <c>true</c> if the provider found evidence the descriptor at this index has
/// been used (a VTXO, an on-chain boarding UTXO, a boltz swap, etc.).
/// </param>
/// <param name="Contracts">
/// Contracts the provider reconstructed and would like the orchestrator to
/// persist. May be empty even when <paramref name="Used"/> is true if the
/// provider only knows "this was used" but not enough to materialize a
/// contract on its own.
/// </param>
public record DiscoveryResult(
    bool Used,
    IReadOnlyList<ArkContract> Contracts)
{
    /// <summary>Convenience: provider saw nothing at this index.</summary>
    public static DiscoveryResult NotFound { get; } = new(false, []);
}

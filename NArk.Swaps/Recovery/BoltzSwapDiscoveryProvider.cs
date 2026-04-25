using Microsoft.Extensions.Logging;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Services;
using NBitcoin.Scripting;

namespace NArk.Swaps.Recovery;

/// <summary>
/// Discovery provider that asks Boltz whether the user pubkey at the given
/// HD derivation index ever participated in a swap as sender or receiver.
/// </summary>
/// <remarks>
/// <para>
/// This provider piggybacks on <see cref="SwapsManagementService.RestoreSwaps"/>
/// — calling it for a single descriptor causes Boltz's <c>/v2/swap/restore</c>
/// to be queried for the descriptor's pubkey. If the call returns any swaps
/// they are reconstructed, persisted as <c>VHTLCContract</c>s, and recorded
/// in <c>ISwapStorage</c> with their original swap-id metadata.
/// </para>
/// <para>
/// We deliberately do NOT return the reconstructed contracts back to the
/// recovery orchestrator: <see cref="SwapsManagementService.RestoreSwaps"/>
/// already imported them with rich <c>Source=swap:&lt;id&gt;</c> metadata, and
/// reusing that path keeps the swap-side bookkeeping correct. The orchestrator
/// only learns that boltz saw usage at this index, which is sufficient for
/// gap-limit accounting.
/// </para>
/// <para>
/// Performance: at most one HTTP call per scanned index. For the typical
/// gap-of-20 scan that is bounded at ~25 calls — fine for a one-time recovery
/// operation.
/// </para>
/// </remarks>
public class BoltzSwapDiscoveryProvider(
    SwapsManagementService swapsManagementService,
    ILogger<BoltzSwapDiscoveryProvider>? logger = null) : IContractDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "boltz";

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken cancellationToken = default)
    {
        var restored = await swapsManagementService.RestoreSwaps(
            wallet.Id, [userDescriptor], cancellationToken);

        if (restored.Count == 0) return DiscoveryResult.NotFound;

        logger?.LogDebug(
            "BoltzSwapDiscoveryProvider: hit at index {Index} — restored {Count} swap(s)",
            index, restored.Count);
        // RestoreSwaps already imported the VHTLC contracts via IContractService.
        // Return Used=true with no extra contracts to avoid double-imports.
        return new DiscoveryResult(true, []);
    }
}

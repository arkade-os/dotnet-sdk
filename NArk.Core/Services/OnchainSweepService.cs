using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;

namespace NArk.Core.Services;

/// <summary>
/// Detects expired boarding/unrolled UTXOs and sweeps them via the unilateral exit path
/// to a fresh boarding address. This is a safety net for UTXOs whose CSV timeout has
/// expired before they were consumed in a batch.
/// </summary>
public class OnchainSweepService(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IChainTimeProvider chainTimeProvider,
    IContractService contractService,
    IWalletProvider walletProvider,
    IOnchainSweepHandler? sweepHandler = null,
    ILogger<OnchainSweepService>? logger = null)
{
    /// <summary>
    /// Scans for expired unrolled/boarding UTXOs and sweeps them.
    /// Call on-demand; caller may wrap in a timer for periodic execution.
    /// </summary>
    public async Task SweepExpiredUtxosAsync(CancellationToken cancellationToken)
    {
        var chainTime = await chainTimeProvider.GetChainTime(cancellationToken);
        logger?.LogDebug("Checking for expired unrolled UTXOs at height {Height}, time {Time}",
            chainTime.Height, chainTime.Timestamp);

        // Get all unspent VTXOs (includeSpent: false is the default)
        var allVtxos = await vtxoStorage.GetVtxos(cancellationToken: cancellationToken);

        // Filter: unrolled and expired
        var expiredUnrolled = allVtxos
            .Where(v => v.Unrolled && !v.IsSpent() && v.IsRecoverable(chainTime))
            .ToList();

        if (expiredUnrolled.Count == 0)
        {
            logger?.LogDebug("No expired unrolled UTXOs found");
            return;
        }

        logger?.LogInformation("Found {Count} expired unrolled UTXOs to sweep", expiredUnrolled.Count);

        // Look up contracts for the expired VTXOs
        var scripts = expiredUnrolled.Select(v => v.Script).Distinct().ToArray();
        var contracts = await contractStorage.GetContracts(
            scripts: scripts,
            contractTypes: [ArkBoardingContract.ContractType],
            cancellationToken: cancellationToken);

        var contractByScript = contracts.ToDictionary(c => c.Script);

        foreach (var vtxo in expiredUnrolled)
        {
            if (!contractByScript.TryGetValue(vtxo.Script, out var contractEntity))
            {
                logger?.LogWarning(
                    "No boarding contract found for expired UTXO {Outpoint}, skipping",
                    vtxo.OutPoint);
                continue;
            }

            try
            {
                await SweepSingleUtxoAsync(vtxo, contractEntity, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "Failed to sweep expired UTXO {Outpoint}", vtxo.OutPoint);
            }
        }
    }

    private async Task SweepSingleUtxoAsync(
        ArkVtxo vtxo,
        ArkContractEntity contractEntity,
        CancellationToken cancellationToken)
    {
        // If a custom handler is registered, give it first shot
        if (sweepHandler is not null)
        {
            var handled = await sweepHandler.HandleExpiredUtxoAsync(
                contractEntity.WalletIdentifier, vtxo, contractEntity, cancellationToken);

            if (handled)
            {
                logger?.LogInformation(
                    "Custom sweep handler processed expired UTXO {Outpoint}",
                    vtxo.OutPoint);
                return;
            }
        }

        // Default behavior: sweep to a fresh boarding address
        logger?.LogInformation(
            "Sweeping expired UTXO {Outpoint} ({Amount} sats) to fresh boarding address",
            vtxo.OutPoint, vtxo.Amount);

        // Derive a fresh boarding contract for the destination
        var freshContract = await contractService.DeriveContract(
            contractEntity.WalletIdentifier,
            NextContractPurpose.Boarding,
            cancellationToken: cancellationToken);

        _ = freshContract; // Will be used as the output address once tx building is implemented

        // TODO: Build sweep transaction
        // 1. Create tx spending via UnilateralPath() of the contract
        // 2. Set sequence to the CSV value
        // 3. Output to fresh boarding address
        // 4. Sign with wallet signer
        // 5. Broadcast via Esplora POST /tx
        throw new NotImplementedException("Sweep transaction building not yet implemented");
    }
}

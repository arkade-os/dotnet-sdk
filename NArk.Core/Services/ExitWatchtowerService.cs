using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Monitors the blockchain for partial tree broadcasts and auto-starts
/// unilateral exits when detected. If someone starts unrolling the tree
/// but doesn't finish, previous owners may claim funds after their CSV
/// timeout. The watchtower detects this and continues the exit automatically.
/// </summary>
public class ExitWatchtowerService(
    IClientTransport transport,
    IVtxoStorage vtxoStorage,
    IVirtualTxStorage virtualTxStorage,
    IOnchainBroadcaster broadcaster,
    IContractStorage contractStorage,
    IWalletProvider walletProvider,
    IContractService contractService,
    UnilateralExitService exitService,
    ILogger<ExitWatchtowerService>? logger = null)
{
    /// <summary>
    /// Check for partial tree broadcasts and auto-start exits.
    /// Call periodically or on block notifications.
    /// </summary>
    public async Task CheckAndRespondAsync(CancellationToken cancellationToken = default)
    {
        // Only check unspent VTXOs that have stored virtual tx branches.
        // This avoids expensive RPC calls for VTXOs without exit data.
        var allVtxos = await vtxoStorage.GetVtxos(cancellationToken: cancellationToken);
        var unspent = allVtxos.Where(v => !v.IsSpent()).ToList();

        foreach (var vtxo in unspent)
        {
            try
            {
                // Skip early if no branch stored — avoids RPC calls
                var hasBranch = await virtualTxStorage.HasBranchAsync(vtxo.OutPoint, cancellationToken);
                if (!hasBranch)
                    continue;

                await CheckVtxoAsync(vtxo, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(0, ex,
                    "Failed to check VTXO {Outpoint} for partial broadcasts", vtxo.OutPoint);
            }
        }
    }

    private async Task CheckVtxoAsync(ArkVtxo vtxo, CancellationToken ct)
    {
        var branch = await virtualTxStorage.GetBranchAsync(vtxo.OutPoint, ct);
        if (branch.Count == 0)
            return;

        // Optimization: check only the root tx first (position 0).
        // If root is not on-chain, nothing downstream can be either.
        var rootTx = branch[0];
        var rootTxid = uint256.Parse(rootTx.Txid);
        var rootStatus = await broadcaster.GetTxStatusAsync(rootTxid, ct);

        if (!rootStatus.Confirmed && !rootStatus.InMempool)
            return; // Root not seen, tree is intact

        // Root is on-chain or in mempool — check if leaf is also confirmed
        var leafTx = branch[^1];
        var leafTxid = uint256.Parse(leafTx.Txid);
        var leafStatus = await broadcaster.GetTxStatusAsync(leafTxid, ct);

        if (leafStatus.Confirmed)
            return; // Full tree already confirmed, exit already in progress or completed

        // Partial broadcast detected! Root is on-chain but leaf is not.
        logger?.LogWarning(
            "Detected partial tree broadcast for VTXO {Outpoint}: root tx {RootTxid} is {RootState} but leaf tx {LeafTxid} is not confirmed",
            vtxo.OutPoint, rootTx.Txid,
            rootStatus.Confirmed ? "confirmed" : "in mempool",
            leafTx.Txid);

        // Find the wallet that owns this VTXO
        var contracts = await contractStorage.GetContracts(
            scripts: [vtxo.Script],
            cancellationToken: ct);
        var contract = contracts.FirstOrDefault();

        if (contract is null)
        {
            logger?.LogWarning("No contract found for VTXO {Outpoint}, cannot auto-start exit",
                vtxo.OutPoint);
            return;
        }

        var walletId = contract.WalletIdentifier;

        // Derive a claim address for the exit using the wallet's address provider
        var claimAddress = await DeriveClaimAddressAsync(walletId, ct);
        if (claimAddress is null)
        {
            logger?.LogWarning(
                "Cannot derive claim address for wallet {WalletId}, cannot auto-start exit for VTXO {Outpoint}",
                walletId, vtxo.OutPoint);
            return;
        }

        logger?.LogWarning(
            "Auto-starting unilateral exit for VTXO {Outpoint} (wallet={WalletId}, claimAddress={Address})",
            vtxo.OutPoint, walletId, claimAddress);

        await exitService.StartExitAsync(walletId, [vtxo.OutPoint], claimAddress, ct);
    }

    /// <summary>
    /// Derives a P2TR on-chain address for claiming exit funds.
    /// Uses the wallet's address provider to derive a boarding contract,
    /// then extracts the on-chain taproot address from it.
    /// </summary>
    private async Task<BitcoinAddress?> DeriveClaimAddressAsync(string walletId, CancellationToken ct)
    {
        try
        {
            var serverInfo = await transport.GetServerInfoAsync(ct);
            var contract = await contractService.DeriveContract(
                walletId,
                NextContractPurpose.Boarding,
                ContractActivityState.Active,
                cancellationToken: ct);

            if (contract is ArkBoardingContract boarding)
                return boarding.GetOnchainAddress(serverInfo.Network);

            // Fallback: derive a receive contract and use its taproot output key
            var spendInfo = contract.GetTaprootSpendInfo();
            return spendInfo.OutputPubKey.GetAddress(serverInfo.Network);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(0, ex, "Failed to derive claim address for wallet {WalletId}", walletId);
            return null;
        }
    }
}

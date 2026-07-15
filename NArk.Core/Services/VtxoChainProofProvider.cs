using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Builds the BIP-322-style ownership proof that authenticates
/// <see cref="IClientTransport.GetVtxoChainAsync"/> against the Arkade indexer. Given a VTXO the
/// wallet owns, it resolves the backing contract, coin and signer and produces the
/// <c>{"type":"get-data"}</c> intent proof arkd expects.
/// </summary>
public interface IVtxoChainProofProvider
{
    /// <summary>
    /// Attempts to build an ownership proof for the VTXO at <paramref name="vtxoOutpoint"/>.
    /// Returns <c>null</c> when the VTXO is unknown to this wallet, its contract cannot be
    /// resolved, or no signer is registered — callers then fall back to the anonymous
    /// (public-exposure) indexer lookup.
    /// </summary>
    Task<(string Proof, string Message)?> TryCreateProofAsync(
        OutPoint vtxoOutpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to build an ownership proof for an already-materialised <paramref name="vtxo"/>,
    /// avoiding a storage round-trip. Returns <c>null</c> under the same conditions as
    /// <see cref="TryCreateProofAsync(OutPoint, CancellationToken)"/>.
    /// </summary>
    Task<(string Proof, string Message)?> TryCreateProofAsync(
        ArkVtxo vtxo, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class VtxoChainProofProvider(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    ICoinService coinService,
    IWalletProvider walletProvider,
    IClientTransport transport,
    ILogger<VtxoChainProofProvider>? logger = null) : IVtxoChainProofProvider
{
    /// <inheritdoc />
    public async Task<(string Proof, string Message)?> TryCreateProofAsync(
        OutPoint vtxoOutpoint, CancellationToken cancellationToken = default)
    {
        var vtxo = (await vtxoStorage.GetVtxos(
                outpoints: [vtxoOutpoint], includeSpent: true, cancellationToken: cancellationToken))
            .FirstOrDefault();

        if (vtxo is null)
        {
            logger?.LogDebug("No tracked VTXO for {Outpoint}; falling back to anonymous chain lookup", vtxoOutpoint);
            return null;
        }

        return await TryCreateProofAsync(vtxo, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(string Proof, string Message)?> TryCreateProofAsync(
        ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        try
        {
            var contract = (await contractStorage.GetContracts(
                    scripts: [vtxo.Script], cancellationToken: cancellationToken))
                .FirstOrDefault();

            if (contract is null)
            {
                logger?.LogDebug(
                    "No contract for VTXO {Outpoint} script; falling back to anonymous chain lookup",
                    vtxo.OutPoint);
                return null;
            }

            var signer = await walletProvider.GetSignerAsync(contract.WalletIdentifier, cancellationToken);
            if (signer is null)
            {
                logger?.LogDebug(
                    "No signer for wallet {WalletId}; falling back to anonymous chain lookup",
                    contract.WalletIdentifier);
                return null;
            }

            var coin = await coinService.GetCoin(contract, vtxo, cancellationToken);
            var network = (await transport.GetServerInfoAsync(cancellationToken)).Network;

            return await IntentProofHelper.CreateGetVtxoChainOwnershipProofAsync(
                coin, signer, network, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Proof construction is best-effort: any resolution failure degrades to the
            // anonymous lookup rather than blocking chain retrieval outright.
            logger?.LogDebug(ex,
                "Failed to build chain ownership proof for VTXO {Outpoint}; falling back to anonymous lookup",
                vtxo.OutPoint);
            return null;
        }
    }
}

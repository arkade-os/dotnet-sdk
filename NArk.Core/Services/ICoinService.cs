using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;

namespace NArk.Core.Services;

/// <summary>
/// Resolves VTXOs into spendable <see cref="ArkCoin"/> instances by matching
/// them with their contract metadata and wallet signing information.
/// </summary>
public interface ICoinService
{
    /// <summary>
    /// Creates a spendable coin from a known contract entity and VTXO.
    /// </summary>
    Task<ArkCoin> GetCoin(ArkContractEntity entity, ArkVtxo vtxo, CancellationToken cancellationToken = default);
    /// <summary>
    /// Creates a spendable coin from a VTXO by looking up the contract from storage.
    /// </summary>
    Task<ArkCoin> GetCoin(ArkVtxo vtxo, string walletIdentifier, CancellationToken cancellationToken = default);
}
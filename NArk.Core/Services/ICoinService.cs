using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;

namespace NArk.Core.Services;

public interface ICoinService
{
    Task<ArkCoin> GetCoin(ArkContractEntity entity, ArkVtxo vtxo, CancellationToken cancellationToken = default);
    Task<ArkCoin> GetCoin(ArkVtxo vtxo, string walletIdentifier, CancellationToken cancellationToken = default);
}
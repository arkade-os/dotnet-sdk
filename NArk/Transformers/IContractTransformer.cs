using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;

namespace NArk.Transformers;

public interface IContractTransformer
{
    Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo);
    Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo);
}
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;

namespace NArk.Core.Transformers;

/// <summary>
/// Transforms a contract + VTXO pair into a spendable <see cref="ArkCoin"/>.
/// Register multiple transformers to handle different contract types
/// (payment contracts, hash-locked contracts, VHTLCs, etc.).
/// </summary>
public interface IContractTransformer
{
    /// <summary>
    /// Returns true if this transformer can handle the given contract/VTXO pair.
    /// </summary>
    Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo);
    /// <summary>
    /// Transforms the contract/VTXO pair into a spendable coin with signing metadata.
    /// </summary>
    Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo);
}
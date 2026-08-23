using NArk.Abstractions.Contracts;

namespace NArk.Swaps.Models;

/// <summary>
/// A swap with its associated contract entity.
/// </summary>
public record ArkSwapWithContract(ArkSwap Swap, ArkContractEntity? Contract);
using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Service for creating and submitting Ark off-chain spend transactions.
/// </summary>
public interface ISpendingService
{
    /// <summary>
    /// Spends specific coins to the given outputs. Returns the Ark transaction ID.
    /// </summary>
    Task<uint256> Spend(string walletId, ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends to the given outputs using automatic coin selection. Returns the Ark transaction ID.
    /// </summary>
    Task<uint256> Spend(string walletId, ArkTxOut[] outputs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all spendable coins for the wallet (unspent, unlocked, and within timelock bounds).
    /// </summary>
    Task<IReadOnlySet<ArkCoin>> GetAvailableCoins(string walletId, CancellationToken cancellationToken = default);
}
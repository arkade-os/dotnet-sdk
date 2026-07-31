using NArk.Abstractions;
using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Service for creating and submitting Ark off-chain spend transactions.
/// </summary>
public interface ISpendingService
{
    /// <summary>
    /// Spends specific coins to the given outputs. Returns the Ark transaction ID.
    /// <paramref name="extensionPackets"/> are merged into the spend's single Extension OP_RETURN
    /// alongside the asset packet and any registered providers (e.g. an Arkade offer packet on a
    /// funding deposit).
    /// </summary>
    Task<uint256> Spend(string walletId, ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default,
        IReadOnlyList<IExtensionPacket>? extensionPackets = null);

    /// <summary>
    /// Spends to the given outputs using automatic coin selection. Returns the Ark transaction ID.
    /// See the coin-specific overload for <paramref name="extensionPackets"/>.
    /// </summary>
    Task<uint256> Spend(string walletId, ArkTxOut[] outputs, CancellationToken cancellationToken = default,
        IReadOnlyList<IExtensionPacket>? extensionPackets = null);

    /// <summary>
    /// Returns all spendable coins for the wallet (unspent, unlocked, and within timelock bounds).
    /// </summary>
    Task<IReadOnlySet<ArkCoin>> GetAvailableCoins(string walletId, CancellationToken cancellationToken = default);
}
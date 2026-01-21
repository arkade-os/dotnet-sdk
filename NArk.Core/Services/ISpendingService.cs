using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.Services;

public interface ISpendingService
{
    Task<uint256> Spend(string walletId, ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
    Task<uint256> Spend(string walletId, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<ArkCoin>> GetAvailableCoins(string walletId, CancellationToken cancellationToken = default);
}
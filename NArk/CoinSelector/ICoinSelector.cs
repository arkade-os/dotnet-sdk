
using NArk.Abstractions;
using NBitcoin;

namespace NArk.CoinSelector;

public interface ICoinSelector
{
    IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetAmount,
        Money dustThreshold,
        int currentSubDustOutputs);
}
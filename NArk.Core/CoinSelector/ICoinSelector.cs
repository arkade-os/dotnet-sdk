
using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector;

/// <summary>
/// Selects coins from a set of available coins to meet a target amount.
/// Implement this to customize UTXO selection strategy (e.g., minimize fees, maximize privacy).
/// </summary>
public interface ICoinSelector
{
    /// <summary>
    /// Selects coins to cover the target amount.
    /// </summary>
    /// <param name="availableCoins">All spendable coins.</param>
    /// <param name="targetAmount">Amount to cover.</param>
    /// <param name="dustThreshold">Minimum value for a change output.</param>
    /// <param name="currentSubDustOutputs">Number of existing sub-dust outputs in the transaction.</param>
    IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetAmount,
        Money dustThreshold,
        int currentSubDustOutputs,
        int maxOpReturnOutputs = 1);

    IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetBtcAmount,
        IReadOnlyList<AssetRequirement> assetRequirements,
        Money dustThreshold,
        int currentSubDustOutputs,
        int maxOpReturnOutputs = 1);
}
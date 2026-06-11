
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
    /// <param name="maxOpReturnOutputs">Maximum OP_RETURN outputs allowed per transaction.</param>
    /// <param name="maxInputs">
    /// Maximum number of inputs the selection may use, or <c>null</c> for no limit.
    /// The Arkade server rejects transactions above its <c>max_tx_weight</c>
    /// (<c>TX_TOO_LARGE</c>), so spends should bound their input count.
    /// Implementations throw <see cref="TooManyInputsException"/> when the target
    /// cannot be covered within the cap.
    /// </param>
    IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetAmount,
        Money dustThreshold,
        int currentSubDustOutputs,
        int maxOpReturnOutputs = 1,
        int? maxInputs = null);

    /// <summary>
    /// Asset-aware coin selection: selects coins carrying the required assets first,
    /// then fills the remaining BTC target. See the BTC-only overload for parameter
    /// semantics, including <paramref name="maxInputs"/>.
    /// </summary>
    IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetBtcAmount,
        IReadOnlyList<AssetRequirement> assetRequirements,
        Money dustThreshold,
        int currentSubDustOutputs,
        int maxOpReturnOutputs = 1,
        int? maxInputs = null);
}

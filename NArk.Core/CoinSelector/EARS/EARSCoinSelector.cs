using NArk.Abstractions;
using NArk.Core.Fees;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

/// <summary>
/// Expiry-Aware Randomized Coin Selector (EARS) for Arkade VTXOs.
/// Groups coins by expiry and runs multiple selection strategies, picking the result with lowest waste.
/// </summary>
public sealed class EARSCoinSelector : ICoinSelector
{
    private readonly CoinSelectionEngine _engine;
    private readonly CoinSelectionPolicy _policy;

    public EARSCoinSelector(CoinSelectionPolicy? policy = null)
    {
        _policy = policy ?? new CoinSelectionPolicy();
        _engine = new CoinSelectionEngine([
            new ExpiryFirstStrategy(),
            new RgliStrategy(),
            new SingleRandomDrawStrategy(),
            new BranchAndBoundStrategy()
        ]);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetAmount,
        Money dustThreshold,
        int currentSubDustOutputs,
        int maxOpReturnOutputs = 1,
        long? maxInputWeightWu = null)
    {
        var candidates = BuildCandidates(availableCoins, dustThreshold);
        var context = BuildContext(targetAmount, dustThreshold, currentSubDustOutputs, maxOpReturnOutputs, null, maxInputWeightWu);
        try
        {
            return _engine.Select(candidates, context, _policy).SelectedCoins;
        }
        catch (NotEnoughFundsException) when (maxInputWeightWu is { } cap
            && candidates.Sum(c => c.Value) >= targetAmount)
        {
            throw new TooManyInputsException(cap);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetBtcAmount,
        IReadOnlyList<AssetRequirement> assetRequirements,
        Money dustThreshold,
        int currentSubDustOutputs,
        int maxOpReturnOutputs = 1,
        long? maxInputWeightWu = null)
    {
        var candidates = BuildCandidates(availableCoins, dustThreshold);
        var context = BuildContext(targetBtcAmount, dustThreshold, currentSubDustOutputs, maxOpReturnOutputs, assetRequirements, maxInputWeightWu);
        try
        {
            return _engine.Select(candidates, context, _policy).SelectedCoins;
        }
        catch (NotEnoughFundsException) when (maxInputWeightWu is { } cap
            && candidates.Sum(c => c.Value) >= targetBtcAmount)
        {
            throw new TooManyInputsException(cap);
        }
    }

    private static IReadOnlyList<CoinCandidate> BuildCandidates(List<ArkCoin> coins, Money dustThreshold) =>
        coins.Where(c => !c.Unrolled).Select(c => new CoinCandidate(
            Coin: c,
            Value: c.TxOut.Value,
            ExpiryGroup: c.ExpiresAtHeight ?? 0u,
            IsDustProne: c.TxOut.Value < dustThreshold,
            Assets: c.Assets ?? [],
            Weight: ArkTxWeightEstimator.GetInputWeightUnits(c)))
        .ToList();

    private static SelectionContext BuildContext(
        Money target,
        Money dust,
        int currentSubDust,
        int maxSubDust,
        IReadOnlyList<AssetRequirement>? assetRequirements = null,
        long? maxInputWeightWu = null,
        bool allowDustInputs = true) =>
        new(TargetAmount: target,
            DustThreshold: dust,
            AllowSubDust: currentSubDust < maxSubDust,
            AllowDustInputs: allowDustInputs,
            MaxInputs: 100,
            CurrentSubDustOutputs: currentSubDust,
            MaxSubDustOutputs: maxSubDust,
            AssetRequirements: assetRequirements ?? [],
            MaxInputWeightWu: maxInputWeightWu);
}

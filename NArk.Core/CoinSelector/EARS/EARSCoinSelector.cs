using NArk.Abstractions;
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
        int maxOpReturnOutputs = 1)
    {
        var context = BuildContext(targetAmount, dustThreshold, currentSubDustOutputs, maxOpReturnOutputs);
        var candidates = BuildCandidates(availableCoins);
        return _engine.Select(candidates, context, _policy).SelectedCoins;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetBtcAmount,
        IReadOnlyList<AssetRequirement> assetRequirements,
        Money dustThreshold,
        int currentSubDustOutputs,
        int maxOpReturnOutputs = 1)
    {
        var context = BuildContext(targetBtcAmount, dustThreshold, currentSubDustOutputs, maxOpReturnOutputs, assetRequirements);
        var candidates = BuildCandidates(availableCoins);
        return _engine.Select(candidates, context, _policy).SelectedCoins;
    }

    private static IReadOnlyList<CoinCandidate> BuildCandidates(List<ArkCoin> coins) =>
        coins.Select(c => new CoinCandidate(
            Coin: c,
            Value: c.TxOut.Value,
            ExpiryGroup: c.ExpiresAtHeight ?? 0u,
            IsDustProne: c.TxOut.Value < 546,
            Assets: c.Assets ?? [],
            Weight: c.TxOut.ScriptPubKey.Length))
        .ToList();

    private static SelectionContext BuildContext(
        Money target,
        Money dust,
        int currentSubDust,
        int maxSubDust,
        IReadOnlyList<AssetRequirement>? assetRequirements = null,
        bool allowDustInputs = true) =>
        new(TargetAmount: target,
            DustThreshold: dust,
            AllowExpiryMixing: false,
            AllowSubDust: currentSubDust < maxSubDust,
            AllowDustInputs: allowDustInputs,
            MaxInputs: 100,
            CurrentSubDustOutputs: currentSubDust,
            MaxSubDustOutputs: maxSubDust,
            AssetRequirements: assetRequirements ?? []);
}

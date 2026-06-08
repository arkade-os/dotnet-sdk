using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed record SelectionContext(
    Money TargetAmount,
    Money DustThreshold,
    bool AllowExpiryMixing,
    bool AllowSubDust,
    bool AllowDustInputs,
    int MaxInputs,
    int CurrentSubDustOutputs,
    int MaxSubDustOutputs,
    IReadOnlyList<AssetRequirement> AssetRequirements);
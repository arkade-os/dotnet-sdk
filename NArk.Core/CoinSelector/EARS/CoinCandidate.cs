using NArk.Abstractions;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed record CoinCandidate(
    ArkCoin Coin,
    Money Value,
    uint ExpiryGroup,
    bool IsDustProne,
    IReadOnlyList<VtxoAsset> Assets,
    int Weight);

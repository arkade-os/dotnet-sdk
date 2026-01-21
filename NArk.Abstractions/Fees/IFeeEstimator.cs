using NArk.Abstractions.Intents;

namespace NArk.Abstractions.Fees;

public interface IFeeEstimator
{
    public Task<long> EstimateFeeAsync(ArkCoin[] coins, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
    public Task<long> EstimateFeeAsync(ArkIntentSpec spec, CancellationToken cancellationToken = default);
}
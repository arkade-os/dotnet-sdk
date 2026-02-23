using NArk.Abstractions.Intents;

namespace NArk.Abstractions.Fees;

/// <summary>
/// Estimates Ark transaction fees in satoshis.
/// </summary>
public interface IFeeEstimator
{
    /// <summary>
    /// Estimates the fee for spending the given coins to the given outputs.
    /// </summary>
    public Task<long> EstimateFeeAsync(ArkCoin[] coins, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
    /// <summary>
    /// Estimates the fee for an intent specification (used during intent generation).
    /// </summary>
    public Task<long> EstimateFeeAsync(ArkIntentSpec spec, CancellationToken cancellationToken = default);
}
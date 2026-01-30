using Microsoft.Extensions.Logging;
using NArk.Swaps.Boltz.Client;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Validates swap amounts and fees against Boltz limits.
/// </summary>
public class BoltzLimitsValidator
{
    private readonly CachedBoltzClient _cachedClient;
    private readonly ILogger<BoltzLimitsValidator>? _logger;

    /// <summary>
    /// Fee tolerance in satoshis for validation. Allows small variations due to rounding.
    /// </summary>
    public const long FeeToleranceSats = 100;

    public BoltzLimitsValidator(CachedBoltzClient cachedClient, ILogger<BoltzLimitsValidator>? logger = null)
    {
        _cachedClient = cachedClient ?? throw new ArgumentNullException(nameof(cachedClient));
        _logger = logger;
    }

    /// <summary>
    /// Validates if an amount is within Boltz limits for the specified swap type.
    /// </summary>
    /// <param name="amountSats">The amount in satoshis.</param>
    /// <param name="isReverse">True for reverse swap (Lightning → Ark), false for submarine (Ark → Lightning).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple indicating if valid and optional error message.</returns>
    public async Task<(bool IsValid, string? Error)> ValidateAmountAsync(
        long amountSats,
        bool isReverse,
        CancellationToken cancellationToken = default)
    {
        var (minAmount, maxAmount, swapType) = await GetLimitsInternalAsync(isReverse, cancellationToken);

        if (minAmount == null || maxAmount == null)
        {
            return (false, "Unable to fetch Boltz limits");
        }

        if (amountSats < minAmount)
        {
            return (false, $"Amount {amountSats} sats is below minimum {minAmount} sats for {swapType} Lightning");
        }

        if (amountSats > maxAmount)
        {
            return (false, $"Amount {amountSats} sats exceeds maximum {maxAmount} sats for {swapType} Lightning");
        }

        return (true, null);
    }

    /// <summary>
    /// Validates if the actual swap fee is within acceptable range compared to expected fee.
    /// </summary>
    /// <param name="amountSats">The invoice/payment amount in satoshis.</param>
    /// <param name="actualSwapAmount">The actual onchain/expected amount from Boltz.</param>
    /// <param name="isReverse">True for reverse swap (Lightning → Ark), false for submarine (Ark → Lightning).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple indicating if fees are valid and optional error message.</returns>
    public async Task<(bool IsValid, string? Error)> ValidateFeesAsync(
        long amountSats,
        long actualSwapAmount,
        bool isReverse,
        CancellationToken cancellationToken = default)
    {
        var (feePercentage, minerFee, swapType) = await GetFeesAsync(isReverse, cancellationToken);

        if (feePercentage == null)
        {
            return (false, "Unable to fetch Boltz fee information");
        }

        // Calculate actual fee based on swap type
        // Reverse: user receives actualSwapAmount onchain, pays amountSats via Lightning
        // Submarine: user pays actualSwapAmount onchain, receives amountSats via Lightning
        var actualFee = isReverse
            ? amountSats - actualSwapAmount  // Reverse: Lightning amount - onchain amount
            : actualSwapAmount - amountSats; // Submarine: onchain amount - Lightning amount

        // Calculate expected fee: (amount × percentage) + miner fee
        var expectedFee = (long)(amountSats * feePercentage.Value) + (minerFee ?? 0);

        // Only fail if actual fee is HIGHER than expected (allow lower fees)
        if (actualFee > expectedFee + FeeToleranceSats)
        {
            _logger?.LogWarning(
                "{SwapType} swap fee too high: expected ~{ExpectedFee} sats ({FeePercentage:P2} + {MinerFee} sats miner fee), got {ActualFee} sats",
                swapType, expectedFee, feePercentage.Value, minerFee ?? 0, actualFee);

            return (false,
                $"Boltz fee verification failed. Expected ~{expectedFee} sats ({feePercentage.Value * 100:F2}% + {minerFee ?? 0} sats miner fee), but swap would charge {actualFee} sats");
        }

        if (actualFee < expectedFee - FeeToleranceSats)
        {
            _logger?.LogInformation(
                "{SwapType} swap fee lower than expected: {ActualFee} sats vs expected {ExpectedFee} sats - accepting",
                swapType, actualFee, expectedFee);
        }

        _logger?.LogDebug(
            "{SwapType} swap fee verified: {ActualFee} sats ({FeePercentage:P2} + {MinerFee} sats miner fee)",
            swapType, actualFee, feePercentage.Value, minerFee ?? 0);

        return (true, null);
    }

    /// <summary>
    /// Gets the current limits for the specified swap type.
    /// </summary>
    public async Task<BoltzLimits?> GetLimitsAsync(bool isReverse, CancellationToken cancellationToken = default)
    {
        if (isReverse)
        {
            var pairs = await _cachedClient.GetReversePairsAsync(cancellationToken);
            if (pairs?.BTC?.ARK == null) return null;

            return new BoltzLimits(
                pairs.BTC.ARK.Limits.Minimal,
                pairs.BTC.ARK.Limits.Maximal,
                pairs.BTC.ARK.Fees.Percentage / 100m, // Convert from percentage to decimal
                pairs.BTC.ARK.Fees.MinerFees?.Claim ?? 0);
        }
        else
        {
            var pairs = await _cachedClient.GetSubmarinePairsAsync(cancellationToken);
            if (pairs?.ARK?.BTC == null) return null;

            return new BoltzLimits(
                pairs.ARK.BTC.Limits.Minimal,
                pairs.ARK.BTC.Limits.Maximal,
                pairs.ARK.BTC.Fees.Percentage / 100m, // Convert from percentage to decimal
                pairs.ARK.BTC.Fees.MinerFeesValue ?? 0);
        }
    }

    /// <summary>
    /// Gets all limits for both submarine and reverse swaps in a single object.
    /// </summary>
    public async Task<BoltzAllLimits?> GetAllLimitsAsync(CancellationToken cancellationToken = default)
    {
        var submarineTask = _cachedClient.GetSubmarinePairsAsync(cancellationToken);
        var reverseTask = _cachedClient.GetReversePairsAsync(cancellationToken);

        await Task.WhenAll(submarineTask, reverseTask);

        var submarinePairs = await submarineTask;
        var reversePairs = await reverseTask;

        if (submarinePairs?.ARK?.BTC == null || reversePairs?.BTC?.ARK == null)
        {
            _logger?.LogWarning("Boltz instance does not support Ark swaps");
            return null;
        }

        return new BoltzAllLimits
        {
            // Submarine: Ark → Lightning (sending)
            SubmarineMinAmount = submarinePairs.ARK.BTC.Limits?.Minimal ?? 0,
            SubmarineMaxAmount = submarinePairs.ARK.BTC.Limits?.Maximal ?? long.MaxValue,
            SubmarineFeePercentage = (submarinePairs.ARK.BTC.Fees?.Percentage ?? 0) / 100m,
            SubmarineMinerFee = submarinePairs.ARK.BTC.Fees?.MinerFeesValue ?? 0,

            // Reverse: Lightning → Ark (receiving)
            ReverseMinAmount = reversePairs.BTC.ARK.Limits?.Minimal ?? 0,
            ReverseMaxAmount = reversePairs.BTC.ARK.Limits?.Maximal ?? long.MaxValue,
            ReverseFeePercentage = (reversePairs.BTC.ARK.Fees?.Percentage ?? 0) / 100m,
            ReverseMinerFee = reversePairs.BTC.ARK.Fees?.MinerFees?.Claim ?? 0,

            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<(long? Min, long? Max, string SwapType)> GetLimitsInternalAsync(
        bool isReverse,
        CancellationToken cancellationToken)
    {
        var swapType = isReverse ? "receiving" : "sending";

        if (isReverse)
        {
            var pairs = await _cachedClient.GetReversePairsAsync(cancellationToken);
            if (pairs?.BTC?.ARK == null)
                return (null, null, swapType);

            return (pairs.BTC.ARK.Limits.Minimal, pairs.BTC.ARK.Limits.Maximal, swapType);
        }
        else
        {
            var pairs = await _cachedClient.GetSubmarinePairsAsync(cancellationToken);
            if (pairs?.ARK?.BTC == null)
                return (null, null, swapType);

            return (pairs.ARK.BTC.Limits.Minimal, pairs.ARK.BTC.Limits.Maximal, swapType);
        }
    }

    private async Task<(decimal? FeePercentage, long? MinerFee, string SwapType)> GetFeesAsync(
        bool isReverse,
        CancellationToken cancellationToken)
    {
        var swapType = isReverse ? "Reverse" : "Submarine";

        if (isReverse)
        {
            var pairs = await _cachedClient.GetReversePairsAsync(cancellationToken);
            if (pairs?.BTC?.ARK == null)
                return (null, null, swapType);

            // Boltz API returns percentage as 0.01 for 0.01%, so divide by 100 to get decimal multiplier
            return (pairs.BTC.ARK.Fees.Percentage / 100m, pairs.BTC.ARK.Fees.MinerFees?.Claim, swapType);
        }
        else
        {
            var pairs = await _cachedClient.GetSubmarinePairsAsync(cancellationToken);
            if (pairs?.ARK?.BTC == null)
                return (null, null, swapType);

            return (pairs.ARK.BTC.Fees.Percentage / 100m, pairs.ARK.BTC.Fees.MinerFeesValue, swapType);
        }
    }
}

/// <summary>
/// Boltz swap limits and fees for a specific direction.
/// </summary>
public record BoltzLimits(
    long MinAmount,
    long MaxAmount,
    decimal FeePercentage,
    long MinerFee);

/// <summary>
/// Combined Boltz limits for both submarine and reverse swaps.
/// </summary>
public class BoltzAllLimits
{
    /// <summary>Submarine swap limits (Ark → Lightning, sending)</summary>
    public long SubmarineMinAmount { get; init; }
    public long SubmarineMaxAmount { get; init; }
    public decimal SubmarineFeePercentage { get; init; }
    public long SubmarineMinerFee { get; init; }

    /// <summary>Reverse swap limits (Lightning → Ark, receiving)</summary>
    public long ReverseMinAmount { get; init; }
    public long ReverseMaxAmount { get; init; }
    public decimal ReverseFeePercentage { get; init; }
    public long ReverseMinerFee { get; init; }

    public DateTimeOffset FetchedAt { get; init; }
}

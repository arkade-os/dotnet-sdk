using Microsoft.Extensions.Logging;
using NArk.Swaps.Boltz.Client;
using NBitcoin;

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
    /// <param name="amount">The amount to validate.</param>
    /// <param name="isReverse">True for reverse swap (Lightning → Ark), false for submarine (Ark → Lightning).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple indicating if valid and optional error message.</returns>
    public async Task<(bool IsValid, string? Error)> ValidateAmountAsync(
        Money amount,
        bool isReverse,
        CancellationToken cancellationToken = default)
    {
        var (minAmount, maxAmount, swapType) = await GetLimitsInternalAsync(isReverse, cancellationToken);

        if (minAmount == null || maxAmount == null)
        {
            return (false, "Unable to fetch Boltz limits");
        }

        if (amount < minAmount)
        {
            return (false, $"Amount {amount.Satoshi} sats is below minimum {minAmount.Satoshi} sats for {swapType} Lightning");
        }

        if (amount > maxAmount)
        {
            return (false, $"Amount {amount.Satoshi} sats exceeds maximum {maxAmount.Satoshi} sats for {swapType} Lightning");
        }

        return (true, null);
    }

    /// <summary>
    /// Validates if the actual swap fee is within acceptable range compared to expected fee.
    /// </summary>
    /// <param name="amount">The invoice/payment amount.</param>
    /// <param name="actualSwapAmount">The actual onchain/expected amount from Boltz.</param>
    /// <param name="isReverse">True for reverse swap (Lightning → Ark), false for submarine (Ark → Lightning).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple indicating if fees are valid and optional error message.</returns>
    public async Task<(bool IsValid, string? Error)> ValidateFeesAsync(
        Money amount,
        Money actualSwapAmount,
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
            ? amount - actualSwapAmount  // Reverse: Lightning amount - onchain amount
            : actualSwapAmount - amount; // Submarine: onchain amount - Lightning amount

        // Calculate expected fee: (amount × percentage) + miner fee. Boltz charges whole
        // satoshis and rounds a fractional percentage cut up, so truncating here would
        // understate what a legitimate swap is allowed to charge and reject it.
        var expectedFee = Money.Satoshis((long)Math.Ceiling(amount.Satoshi * feePercentage.Value))
                          + (minerFee ?? Money.Zero);

        // Only fail if actual fee is HIGHER than expected (allow lower fees)
        if (actualFee > expectedFee + FeeToleranceSats)
        {
            _logger?.LogWarning(
                "{SwapType} swap fee too high: expected ~{ExpectedFee} sats ({FeePercentage:P2} + {MinerFee} sats miner fee), got {ActualFee} sats",
                swapType, expectedFee.Satoshi, feePercentage.Value, (minerFee ?? Money.Zero).Satoshi, actualFee.Satoshi);

            return (false,
                $"Boltz fee verification failed. Expected ~{expectedFee.Satoshi} sats ({feePercentage.Value * 100:F2}% + {(minerFee ?? Money.Zero).Satoshi} sats miner fee), but swap would charge {actualFee.Satoshi} sats");
        }

        if (actualFee < expectedFee - FeeToleranceSats)
        {
            _logger?.LogInformation(
                "{SwapType} swap fee lower than expected: {ActualFee} sats vs expected {ExpectedFee} sats - accepting",
                swapType, actualFee.Satoshi, expectedFee.Satoshi);
        }

        _logger?.LogDebug(
            "{SwapType} swap fee verified: {ActualFee} sats ({FeePercentage:P2} + {MinerFee} sats miner fee)",
            swapType, actualFee.Satoshi, feePercentage.Value, (minerFee ?? Money.Zero).Satoshi);

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
                Money.Satoshis(pairs.BTC.ARK.Limits.Minimal),
                Money.Satoshis(pairs.BTC.ARK.Limits.Maximal),
                pairs.BTC.ARK.Fees.Percentage / 100m, // Convert from percentage to decimal
                Money.Satoshis(pairs.BTC.ARK.Fees.MinerFees?.Claim ?? 0));
        }
        else
        {
            var pairs = await _cachedClient.GetSubmarinePairsAsync(cancellationToken);
            if (pairs?.ARK?.BTC == null) return null;

            return new BoltzLimits(
                Money.Satoshis(pairs.ARK.BTC.Limits.Minimal),
                Money.Satoshis(pairs.ARK.BTC.Limits.Maximal),
                pairs.ARK.BTC.Fees.Percentage / 100m, // Convert from percentage to decimal
                Money.Satoshis(pairs.ARK.BTC.Fees.MinerFeesValue ?? 0));
        }
    }

    /// <summary>
    /// Gets the current limits for chain swaps.
    /// </summary>
    /// <param name="isBtcToArk">True for BTC→ARK, false for ARK→BTC.</param>
    public async Task<BoltzLimits?> GetChainLimitsAsync(bool isBtcToArk, CancellationToken cancellationToken = default)
    {
        var pairs = await _cachedClient.GetChainPairsAsync(cancellationToken);

        var pairDetails = isBtcToArk
            ? pairs?.BTC?.ARK
            : pairs?.ARK?.BTC;

        if (pairDetails == null) return null;

        return new BoltzLimits(
            Money.Satoshis(pairDetails.Limits.Minimal),
            Money.Satoshis(pairDetails.Limits.Maximal),
            pairDetails.Fees.Percentage / 100m,
            Money.Satoshis(pairDetails.Fees.MinerFees.User.Lockup + pairDetails.Fees.MinerFees.Server));
    }

    /// <summary>
    /// Gets all limits for submarine, reverse, and chain swaps in a single object.
    /// </summary>
    public async Task<BoltzAllLimits?> GetAllLimitsAsync(CancellationToken cancellationToken = default)
    {
        var submarineTask = _cachedClient.GetSubmarinePairsAsync(cancellationToken);
        var reverseTask = _cachedClient.GetReversePairsAsync(cancellationToken);
        var chainTask = _cachedClient.GetChainPairsAsync(cancellationToken);

        await Task.WhenAll(submarineTask, reverseTask, chainTask);

        var submarinePairs = await submarineTask;
        var reversePairs = await reverseTask;
        var chainPairs = await chainTask;

        if (submarinePairs?.ARK?.BTC == null || reversePairs?.BTC?.ARK == null)
        {
            _logger?.LogWarning("Boltz instance does not support Ark swaps");
            return null;
        }

        var limits = new BoltzAllLimits
        {
            // Submarine: Ark → Lightning (sending)
            SubmarineMinAmount = Money.Satoshis(submarinePairs.ARK.BTC.Limits?.Minimal ?? 0),
            SubmarineMaxAmount = Money.Satoshis(submarinePairs.ARK.BTC.Limits?.Maximal ?? Money.Coins(21_000_000m).Satoshi),
            SubmarineFeePercentage = (submarinePairs.ARK.BTC.Fees?.Percentage ?? 0) / 100m,
            SubmarineMinerFee = Money.Satoshis(submarinePairs.ARK.BTC.Fees?.MinerFeesValue ?? 0),

            // Reverse: Lightning → Ark (receiving)
            ReverseMinAmount = Money.Satoshis(reversePairs.BTC.ARK.Limits?.Minimal ?? 0),
            ReverseMaxAmount = Money.Satoshis(reversePairs.BTC.ARK.Limits?.Maximal ?? Money.Coins(21_000_000m).Satoshi),
            ReverseFeePercentage = (reversePairs.BTC.ARK.Fees?.Percentage ?? 0) / 100m,
            ReverseMinerFee = Money.Satoshis(reversePairs.BTC.ARK.Fees?.MinerFees?.Claim ?? 0),

            FetchedAt = DateTimeOffset.UtcNow
        };

        // Chain: BTC ↔ ARK (optional — may not be supported)
        var btcToArk = chainPairs?.BTC?.ARK;
        if (btcToArk != null)
        {
            limits.ChainBtcToArkMinAmount = Money.Satoshis(btcToArk.Limits.Minimal);
            limits.ChainBtcToArkMaxAmount = Money.Satoshis(btcToArk.Limits.Maximal);
            limits.ChainBtcToArkFeePercentage = btcToArk.Fees.Percentage / 100m;
            limits.ChainBtcToArkMinerFee = Money.Satoshis(btcToArk.Fees.MinerFees.User.Lockup + btcToArk.Fees.MinerFees.Server);
        }

        var arkToBtc = chainPairs?.ARK?.BTC;
        if (arkToBtc != null)
        {
            limits.ChainArkToBtcMinAmount = Money.Satoshis(arkToBtc.Limits.Minimal);
            limits.ChainArkToBtcMaxAmount = Money.Satoshis(arkToBtc.Limits.Maximal);
            limits.ChainArkToBtcFeePercentage = arkToBtc.Fees.Percentage / 100m;
            limits.ChainArkToBtcMinerFee = Money.Satoshis(arkToBtc.Fees.MinerFees.User.Lockup + arkToBtc.Fees.MinerFees.Server);
        }

        return limits;
    }

    private async Task<(Money? Min, Money? Max, string SwapType)> GetLimitsInternalAsync(
        bool isReverse,
        CancellationToken cancellationToken)
    {
        var swapType = isReverse ? "receiving" : "sending";

        if (isReverse)
        {
            var pairs = await _cachedClient.GetReversePairsAsync(cancellationToken);
            if (pairs?.BTC?.ARK == null)
                return (null, null, swapType);

            return (Money.Satoshis(pairs.BTC.ARK.Limits.Minimal), Money.Satoshis(pairs.BTC.ARK.Limits.Maximal), swapType);
        }
        else
        {
            var pairs = await _cachedClient.GetSubmarinePairsAsync(cancellationToken);
            if (pairs?.ARK?.BTC == null)
                return (null, null, swapType);

            return (Money.Satoshis(pairs.ARK.BTC.Limits.Minimal), Money.Satoshis(pairs.ARK.BTC.Limits.Maximal), swapType);
        }
    }

    private async Task<(decimal? FeePercentage, Money? MinerFee, string SwapType)> GetFeesAsync(
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
            return (pairs.BTC.ARK.Fees.Percentage / 100m,
                pairs.BTC.ARK.Fees.MinerFees is { Claim: var claim } ? Money.Satoshis(claim) : null, swapType);
        }
        else
        {
            var pairs = await _cachedClient.GetSubmarinePairsAsync(cancellationToken);
            if (pairs?.ARK?.BTC == null)
                return (null, null, swapType);

            return (pairs.ARK.BTC.Fees.Percentage / 100m,
                pairs.ARK.BTC.Fees.MinerFeesValue is { } minerFees ? Money.Satoshis(minerFees) : null, swapType);
        }
    }
}

/// <summary>
/// Boltz swap limits and fees for a specific direction.
/// </summary>
public record BoltzLimits(
    Money MinAmount,
    Money MaxAmount,
    /// <summary>
    /// Fee as a decimal fraction (e.g. <c>0.005</c> for 0.5%) — NOT percent.
    /// Boltz's wire <c>Percentage</c> field is in percent and this record
    /// normalises it to a fraction at construction so callers can multiply
    /// directly: <c>fee = amount * FeePercentage</c>.
    /// </summary>
    decimal FeePercentage,
    Money MinerFee);

/// <summary>
/// Combined Boltz limits for submarine, reverse, and chain swaps.
/// </summary>
public class BoltzAllLimits
{
    /// <summary>Submarine swap limits (Ark → Lightning, sending)</summary>
    public Money SubmarineMinAmount { get; init; } = Money.Zero;
    public Money SubmarineMaxAmount { get; init; } = Money.Zero;
    public decimal SubmarineFeePercentage { get; init; }
    public Money SubmarineMinerFee { get; init; } = Money.Zero;

    /// <summary>Reverse swap limits (Lightning → Ark, receiving)</summary>
    public Money ReverseMinAmount { get; init; } = Money.Zero;
    public Money ReverseMaxAmount { get; init; } = Money.Zero;
    public decimal ReverseFeePercentage { get; init; }
    public Money ReverseMinerFee { get; init; } = Money.Zero;

    /// <summary>Chain swap limits (BTC → ARK, on-chain to Ark)</summary>
    public Money? ChainBtcToArkMinAmount { get; set; }
    public Money? ChainBtcToArkMaxAmount { get; set; }
    public decimal? ChainBtcToArkFeePercentage { get; set; }
    public Money? ChainBtcToArkMinerFee { get; set; }

    /// <summary>Chain swap limits (ARK → BTC, Ark to on-chain)</summary>
    public Money? ChainArkToBtcMinAmount { get; set; }
    public Money? ChainArkToBtcMaxAmount { get; set; }
    public decimal? ChainArkToBtcFeePercentage { get; set; }
    public Money? ChainArkToBtcMinerFee { get; set; }

    /// <summary>Whether chain swaps are available.</summary>
    public bool ChainSwapsAvailable => ChainBtcToArkMinAmount is not null || ChainArkToBtcMinAmount is not null;

    public DateTimeOffset FetchedAt { get; init; }
}

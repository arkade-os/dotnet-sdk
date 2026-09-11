using System.Numerics;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace NArk.ArkadeIntents.Evm;

/// <summary>Locally configured facts and timing bounds for one EVM send market.</summary>
public sealed record EvmSendPolicy
{
    /// <summary>Expected EIP-155 chain identifier.</summary>
    public required BigInteger ChainId { get; init; }

    /// <summary>Expected lowercase ERC20 token address.</summary>
    public required string TokenAddress { get; init; }

    /// <summary>Expected ERC20Swap contract address.</summary>
    public required string SwapContractAddress { get; init; }

    /// <summary>Shortest plausible block interval, in seconds.</summary>
    public required decimal FastestSecondsPerBlock { get; init; }

    /// <summary>Longest plausible block interval, in seconds.</summary>
    public required decimal SlowestSecondsPerBlock { get; init; }

    /// <summary>Exact lock depth the client accepts.</summary>
    public required int MinConfirmations { get; init; }

    /// <summary>Exact minimum age the client accepts, in seconds.</summary>
    public required int MinAgeSeconds { get; init; }

    /// <summary>Minimum time left before the earliest plausible EVM expiry.</summary>
    public int MinimumClaimWindowSeconds { get; init; } = 1_800;

    /// <summary>Required delay between the latest plausible EVM expiry and Arkade refund.</summary>
    public int ArkadeRefundMarginSeconds { get; init; } = 7_200;

    /// <summary>Whether funding requires the nine-leaf emulator refund path.</summary>
    public bool RequireEmulatorRefundPath { get; init; } = true;

    internal (string TokenAddress, string SwapContractAddress) Validate()
    {
        if (ChainId <= 0)
            throw new ArgumentOutOfRangeException(nameof(ChainId));
        var token = EvmWire.RequireNonZeroAddress(TokenAddress, nameof(TokenAddress), lowerCase: true);
        var contract = EvmWire.RequireNonZeroAddress(
            SwapContractAddress, nameof(SwapContractAddress), lowerCase: true);
        if (FastestSecondsPerBlock <= 0 || SlowestSecondsPerBlock < FastestSecondsPerBlock)
            throw new ArgumentException("block cadence policy is invalid");
        if (MinConfirmations < 1 || MinAgeSeconds < 0 || MinimumClaimWindowSeconds < 0
            || ArkadeRefundMarginSeconds < 0)
            throw new ArgumentException("proof and deadline policy is invalid");
        return (token, contract);
    }

    internal static bool ProductIsLessThan(BigInteger count, decimal multiplier, BigInteger threshold)
    {
        var (numerator, denominator) = Fraction(multiplier);
        return count * numerator < threshold * denominator;
    }

    internal static bool ProductIsGreaterThan(BigInteger count, decimal multiplier, BigInteger threshold)
    {
        var (numerator, denominator) = Fraction(multiplier);
        return count * numerator > threshold * denominator;
    }

    internal void RequireExecutionWindow(BigInteger timeoutBlock, BigInteger currentBlock,
        long? arkadeRefundLocktime, long now)
    {
        var remaining = timeoutBlock - currentBlock;
        if (remaining <= 0 || ProductIsLessThan(remaining, FastestSecondsPerBlock, MinimumClaimWindowSeconds))
            throw new EvmSwapProofException("ERC20 refund height leaves too little claim time");
        if (arkadeRefundLocktime is not { } refundAt) return;
        var available = new BigInteger(refundAt - now) - ArkadeRefundMarginSeconds;
        if (available < 0 || ProductIsGreaterThan(remaining, SlowestSecondsPerBlock, available))
            throw new EvmSwapProofException("Arkade refund no longer preserves the configured EVM margin");
    }

    private static (BigInteger Numerator, BigInteger Denominator) Fraction(decimal value)
    {
        var bits = decimal.GetBits(value);
        var numerator = ((BigInteger)(uint)bits[2] << 64)
            | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        var scale = (bits[3] >> 16) & 0x7f;
        return (numerator, BigInteger.Pow(10, scale));
    }
}

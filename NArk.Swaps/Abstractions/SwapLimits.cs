using NBitcoin;

namespace NArk.Swaps.Abstractions;

public record SwapLimits
{
    public required SwapRoute Route { get; init; }
    public required Money MinAmount { get; init; }
    public required Money MaxAmount { get; init; }
    public required decimal FeePercentage { get; init; }
    public required Money MinerFee { get; init; }
}

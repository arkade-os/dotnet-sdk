namespace NArk.Swaps.Abstractions;

public record SwapLimits
{
    public required SwapRoute Route { get; init; }
    public required long MinAmount { get; init; }
    public required long MaxAmount { get; init; }
    public required decimal FeePercentage { get; init; }
    public required long MinerFee { get; init; }
}

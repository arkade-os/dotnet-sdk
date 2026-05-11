namespace NArk.Swaps.Abstractions;

public record SwapQuote
{
    public required SwapRoute Route { get; init; }
    public required long SourceAmount { get; init; }
    public required long DestinationAmount { get; init; }
    public required long TotalFees { get; init; }
    public required decimal ExchangeRate { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
}

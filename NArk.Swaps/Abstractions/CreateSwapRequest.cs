namespace NArk.Swaps.Abstractions;

public abstract record CreateSwapRequest
{
    public required string WalletId { get; init; }
    public required SwapRoute Route { get; init; }
    public required long Amount { get; init; }
    public string? PreferredProviderId { get; init; }
}

public record LightningSwapRequest : CreateSwapRequest
{
    public required string Invoice { get; init; }
}

public record EvmSwapRequest : CreateSwapRequest
{
    public required string EvmAddress { get; init; }
    public required string TokenContract { get; init; }
}

public record OnchainSwapRequest : CreateSwapRequest
{
    public string? DestinationAddress { get; init; }
}

public record SimpleSwapRequest : CreateSwapRequest;

using NArk.Swaps.Models;

namespace NArk.Swaps.Abstractions;

public abstract record SwapResult
{
    public required string SwapId { get; init; }
    public required string ProviderId { get; init; }
    public required SwapRoute Route { get; init; }
    public required long Amount { get; init; }
    public required ArkSwapStatus Status { get; init; }
    public DateTimeOffset? Expiry { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public record DepositSwapResult : SwapResult
{
    public required string DepositAddress { get; init; }
}

public record InvoiceSwapResult : SwapResult
{
    public required string Invoice { get; init; }
}

public record VhtlcSwapResult : SwapResult
{
    public required string ContractScript { get; init; }
    public required string ContractAddress { get; init; }
}

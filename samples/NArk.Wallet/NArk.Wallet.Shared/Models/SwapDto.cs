namespace NArk.Wallet.Shared.Models;

public record SwapDto(
    string SwapId,
    string SwapType,
    string Status,
    long ExpectedAmountSats,
    string? Invoice,
    string? FailReason,
    DateTimeOffset CreatedAt);

public record CreateSwapRequest(
    string WalletId,
    long AmountSats,
    string SwapType);

public record CreateSwapResponse(string SwapId, string? Invoice, string? Address);

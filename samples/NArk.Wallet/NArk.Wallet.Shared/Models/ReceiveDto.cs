namespace NArk.Wallet.Shared.Models;

public record ReceiveInfoResponse(
    string ArkAddress,
    string BoardingAddress,
    string? LnurlPayUrl);

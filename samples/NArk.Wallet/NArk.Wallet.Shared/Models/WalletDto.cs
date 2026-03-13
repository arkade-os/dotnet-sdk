namespace NArk.Wallet.Shared.Models;

public record WalletDto(string Id, string WalletType, string? Destination, long BalanceSats);

public record CreateWalletRequest(string? Secret, string WalletType = "SingleKey");

public record CreateWalletResponse(string WalletId, string ArkAddress);

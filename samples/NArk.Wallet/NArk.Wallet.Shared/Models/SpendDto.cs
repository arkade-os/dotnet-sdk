namespace NArk.Wallet.Shared.Models;

public record SpendRequest(string WalletId, string DestinationAddress, long AmountSats);

public record SpendResponse(string TxId);

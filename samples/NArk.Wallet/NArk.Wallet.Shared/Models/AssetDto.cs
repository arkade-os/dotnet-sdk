namespace NArk.Wallet.Shared.Models;

public record AssetDto(string AssetId, string? Name, ulong TotalBalance);

public record IssueAssetRequest(
    string WalletId,
    ulong Amount,
    string? ControlAssetId,
    Dictionary<string, string>? Metadata);

public record IssueAssetResponse(string TxId, string AssetId);

public record BurnAssetRequest(string WalletId, string AssetId, ulong Amount);

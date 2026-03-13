namespace NArk.Wallet.Shared.Models;

public record VtxoDto(
    string TransactionId,
    uint OutputIndex,
    long AmountSats,
    bool IsSpent,
    bool IsExpired,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    List<VtxoAssetDto>? Assets);

public record VtxoAssetDto(string AssetId, ulong Amount);

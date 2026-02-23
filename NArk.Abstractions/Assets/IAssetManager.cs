namespace NArk.Abstractions.Assets;

public record IssuanceResult(string ArkTxId, string AssetId);

public record IssuanceParams(
    ulong Amount,
    string? ControlAssetId = null,
    Dictionary<string, string>? Metadata = null);

public record ReissuanceParams(string AssetId, ulong Amount);

public record BurnParams(string AssetId, ulong Amount);

public interface IAssetManager
{
    Task<IssuanceResult> IssueAsync(string walletId, IssuanceParams parameters,
        CancellationToken cancellationToken = default);

    Task<string> ReissueAsync(string walletId, ReissuanceParams parameters,
        CancellationToken cancellationToken = default);

    Task<string> BurnAsync(string walletId, BurnParams parameters,
        CancellationToken cancellationToken = default);
}

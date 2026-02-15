namespace NArk.Core.Transport.Models;

public record ArkAssetDetails(
    string AssetId,
    ulong Supply,
    string? ControlAssetId,
    IReadOnlyDictionary<string, string>? Metadata);

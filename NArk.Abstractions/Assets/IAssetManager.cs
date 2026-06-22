namespace NArk.Abstractions.Assets;

/// <summary>Result of an asset issuance: the Arkade transaction ID and the newly minted asset ID.</summary>
public record IssuanceResult(string ArkTxId, string AssetId);

/// <summary>Parameters for issuing a new Arkade asset.</summary>
/// <param name="Amount">Token supply to mint.</param>
/// <param name="ControlAssetId">Asset ID that gates further issuance; null for uncontrolled supply.</param>
/// <param name="Metadata">Optional key-value metadata attached to the issuance.</param>
public record IssuanceParams(
    ulong Amount,
    string? ControlAssetId = null,
    Dictionary<string, string>? Metadata = null);

/// <summary>Parameters for minting additional supply of an existing asset.</summary>
public record ReissuanceParams(string AssetId, ulong Amount);

/// <summary>Parameters for destroying a quantity of an existing asset.</summary>
public record BurnParams(string AssetId, ulong Amount);

/// <summary>Issues, reissues, and burns Arkade assets.</summary>
public interface IAssetManager
{
    /// <summary>Issues a new asset for the given wallet. Returns the Arkade transaction ID and asset ID.</summary>
    Task<IssuanceResult> IssueAsync(string walletId, IssuanceParams parameters,
        CancellationToken cancellationToken = default);

    /// <summary>Mints additional supply of an existing asset. Returns the Arkade transaction ID.</summary>
    Task<string> ReissueAsync(string walletId, ReissuanceParams parameters,
        CancellationToken cancellationToken = default);

    /// <summary>Burns a quantity of an existing asset. Returns the Arkade transaction ID.</summary>
    Task<string> BurnAsync(string walletId, BurnParams parameters,
        CancellationToken cancellationToken = default);
}

using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore;

/// <summary>
/// Configuration options for Ark EF Core entity mapping.
/// </summary>
public class ArkStorageOptions
{
    /// <summary>
    /// Database schema. Default: "ark".
    /// BTCPay sets this to "BTCPayServer.Plugins.Ark" for backwards compatibility.
    /// </summary>
    public string? Schema { get; set; } = "ark";

    public string WalletsTable { get; set; } = "Wallets";
    public string WalletContractsTable { get; set; } = "WalletContracts";
    public string VtxosTable { get; set; } = "Vtxos";
    public string IntentsTable { get; set; } = "Intents";
    public string IntentVtxosTable { get; set; } = "IntentVtxos";
    public string SwapsTable { get; set; } = "Swaps";
    public string PaymentsTable { get; set; } = "Payments";
    public string PaymentRequestsTable { get; set; } = "PaymentRequests";

    /// <summary>
    /// Optional callback for provider-specific text search on contract metadata.
    /// PostgreSQL consumers can provide ILIKE on jsonb. Others fall back to LINQ .Contains().
    /// </summary>
    public Func<IQueryable<ArkWalletContractEntity>, string, IQueryable<ArkWalletContractEntity>>?
        ContractSearchProvider { get; set; }
}

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
    public string VirtualTxsTable { get; set; } = "VirtualTxs";
    public string VtxoBranchesTable { get; set; } = "VtxoBranches";
    public string ExitSessionsTable { get; set; } = "ExitSessions";

    /// <summary>
    /// Optional callback for provider-specific text search on contract metadata.
    /// PostgreSQL consumers can provide ILIKE on jsonb. Others fall back to LINQ .Contains().
    /// </summary>
    public Func<IQueryable<ArkWalletContractEntity>, string, IQueryable<ArkWalletContractEntity>>?
        ContractSearchProvider
    { get; set; }

    /// <summary>
    /// When true, stores every <see cref="DateTimeOffset"/> column as <see cref="long"/>
    /// UTC ticks (BIGINT on Postgres/MSSQL, INTEGER on SQLite). Needed for SQLite consumers
    /// because EF Core's SQLite provider rejects <c>ORDER BY</c> on the default TEXT
    /// representation of <see cref="DateTimeOffset"/> — every paged query in this SDK
    /// (<c>GetVtxos</c>, <c>GetContracts</c>, <c>GetIntents</c>, …) breaks otherwise.
    ///
    /// <para>
    /// <b>Off by default</b> to preserve native column types (<c>timestamptz</c> / <c>datetimeoffset</c>)
    /// for existing Postgres/MSSQL consumers. Enabling this is a schema change: stored values
    /// switch from TEXT/timestamp to BIGINT and the original offset is dropped (read-back is
    /// always UTC, offset zero). On-disk size is unchanged.
    /// </para>
    ///
    /// <para>
    /// Migration path for existing SQLite consumers:
    /// <list type="number">
    /// <item>If you can drop the DB (e.g. local cache), delete the file and let <c>EnsureCreated</c>
    /// re-create the schema with INTEGER columns.</item>
    /// <item>Otherwise run a one-off SQL migration to convert TEXT columns to INTEGER ticks
    /// before enabling this flag.</item>
    /// </list>
    /// </para>
    /// </summary>
    public bool StoreDateTimeOffsetAsTicks { get; set; }
}

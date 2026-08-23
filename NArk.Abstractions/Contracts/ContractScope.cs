namespace NArk.Abstractions.Contracts;

/// <summary>
/// Identifies the layer a contract's funds live on: on-chain (a boarding UTXO,
/// synced/swept via Esplora/NBXplorer) or off-chain (a VTXO, synced via the
/// Arkade indexer). A contract whose funds can appear on both layers uses
/// <see cref="Onchain"/> | <see cref="Offchain"/>.
/// </summary>
/// <remarks>
/// Query this only via the bitwise form <c>(Scope &amp; X) == X</c>, which EF Core
/// translates to SQL across Npgsql, SQLite and SqlServer. A value carrying both
/// flags therefore satisfies an <see cref="Onchain"/>-include and an
/// <see cref="Offchain"/>-include filter alike. Never use <c>HasFlag</c>: EF Core
/// does not translate it (it client-evaluates or throws).
/// </remarks>
[Flags]
public enum ContractScope
{
    /// <summary>Funds live on-chain (a boarding UTXO).</summary>
    Onchain = 1,

    /// <summary>Funds live off-chain (a VTXO synced via the Arkade indexer).</summary>
    Offchain = 2,
}

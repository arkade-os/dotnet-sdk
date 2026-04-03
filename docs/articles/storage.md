# Storage (EF Core)

## Setup

```csharp
services.AddDbContextFactory<YourDbContext>(opts =>
    opts.UseNpgsql(connectionString));

services.AddArkEfCoreStorage<YourDbContext>();
```

Your `DbContext` must inherit from or include the Ark entity configuration:

```csharp
public class YourDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddArkEntities(); // Adds all Ark tables
    }
}
```

## Entity Reference

| Table | Entity | Description |
|---|---|---|
| Wallets | `WalletEntity` | HD/SingleKey wallet configurations |
| VTXOs | `VtxoEntity` | Virtual UTXO state, amounts, expiry, spend status |
| Contracts | `ContractEntity` | Taproot contracts with derivation indices and metadata |
| Intents | `IntentEntity` | Batch payment intents and lifecycle |
| Swaps | `SwapEntity` | Boltz swap state and metadata |

## Storage Interfaces

Each storage interface can be implemented independently:

- `IVtxoStorage` — VTXO CRUD and queries
- `IContractStorage` — Contract management and script lookups
- `IIntentStorage` — Intent lifecycle management
- `ISwapStorage` — Swap state tracking
- `IWalletStorage` — Wallet persistence

## Provider Agnostic

The EF Core storage works with any EF Core provider:

```csharp
// PostgreSQL
opts.UseNpgsql(connectionString);

// SQLite (e.g., for mobile/desktop apps)
opts.UseSqlite("Data Source=ark.db");

// In-memory (testing)
opts.UseInMemoryDatabase("test");
```

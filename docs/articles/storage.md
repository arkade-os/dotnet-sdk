# Storage (EF Core)

`NArk.Storage.EfCore` provides ready-made storage implementations. It is **provider-agnostic** — no dependency on Npgsql or any specific database driver.

## Setup

Two pieces wire storage up: a `DbContext` that includes the Arkade entity configuration, and the `AddArkEfCoreStorage<TDbContext>()` DI registration.

```csharp
public class MyDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureArkEntities(opts =>
        {
            opts.Schema = "ark";            // default; set to null for no schema
            // opts.WalletsTable = "Wallets"; // all table names configurable
        });
    }
}

services.AddDbContextFactory<MyDbContext>(opts =>
    opts.UseNpgsql(connectionString));

services.AddArkEfCoreStorage<MyDbContext>(opts =>
{
    opts.Schema = "ark";
});
```

`AddArkEfCoreStorage` registers all the core storage implementations (`IVtxoStorage`, `IContractStorage`, `IIntentStorage`, `ISwapStorage`, `IWalletStorage`).

## Core Entities

| Entity | Table | Primary Key |
|---|---|---|
| `ArkWalletEntity` | `Wallets` | `Id` |
| `ArkWalletContractEntity` | `WalletContracts` | `(Script, WalletId)` |
| `VtxoEntity` | `Vtxos` | `(TransactionId, TransactionOutputIndex)` |
| `ArkIntentEntity` | `Intents` | `IntentTxId` |
| `ArkIntentVtxoEntity` | `IntentVtxos` | `(IntentTxId, VtxoTransactionId, VtxoTransactionOutputIndex)` |
| `ArkSwapEntity` | `Swaps` | `(SwapId, WalletId)` |
| `ArkSyncStateEntity` | `SyncState` | `Id` (singleton row, key=`"vtxo"`) |

`ArkSyncStateEntity` persists the `LastFullPollAt` cursor used by `VtxoSynchronizationService` to bound the cold-start catch-up window. Without it, a process restart on a wallet with thousands of historical VTXOs re-fetches the entire script set from arkd. With it, the first catch-up poll uses the stored timestamp as its `after` filter so only changes since the last shutdown are returned. The cursor is advanced when the cold-start catch-up succeeds, and after every subsequent successful 5-second routine poll. If the catch-up fails (transient indexer/network error) the cursor stays unchanged and routine polls do **not** advance it until catch-up has succeeded at least once — this prevents a failure-then-success sequence from skipping the catch-up window.

## Payment Tracking (Opt-In)

Payment tracking (`ArkPayment` / `ArkPaymentRequest`) is **opt-in** — consumers who don't need it carry no extra schema or services. To enable it, add the entity configuration *and* the DI registration:

```csharp
// OnModelCreating — alongside ConfigureArkEntities
modelBuilder.ConfigureArkEntities(opts => opts.Schema = "ark");
modelBuilder.ConfigureArkPaymentEntities(opts => opts.Schema = "ark");

// DI — alongside AddArkEfCoreStorage
services.AddArkEfCoreStorage<MyDbContext>();
services.AddArkPaymentTracking();
```

`AddArkPaymentTracking()` registers `IPaymentStorage`, `IPaymentRequestStorage`, and `PaymentTrackingService` as an `IHostedService` so its `VtxosChanged`/`IntentChanged`/`SwapsChanged` subscriptions activate on startup. After calling `ConfigureArkPaymentEntities`, run the corresponding EF Core migration so the `Payments` and `PaymentRequests` tables are created.

Payment-tracking entities:

| Entity | Table | Primary Key |
|---|---|---|
| `ArkPaymentEntity` | `Payments` | `PaymentId` |
| `ArkPaymentRequestEntity` | `PaymentRequests` | `RequestId` |

## Storage Interfaces

Each interface can be implemented independently if you need a non-EF Core backend:

- `IVtxoStorage` — VTXO CRUD and queries
- `IContractStorage` — contract management and script lookups
- `IIntentStorage` — intent lifecycle management
- `ISwapStorage` — swap state tracking
- `IWalletStorage` — wallet persistence
- `IPaymentStorage` / `IPaymentRequestStorage` — opt-in payment tracking

## Provider Agnostic

Works with any EF Core provider — choose at `DbContextFactory` registration time:

```csharp
// PostgreSQL
opts.UseNpgsql(connectionString);

// SQLite (mobile/desktop apps)
opts.UseSqlite("Data Source=ark.db");

// In-memory (testing only — some queries use provider-specific features)
opts.UseInMemoryDatabase("test");
```

`ArkStorageOptions.ContractSearchProvider` lets you inject provider-specific text-search for contract metadata (e.g. PostgreSQL `ILIKE`). See `ArkStorageOptions` for all configuration knobs.

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

`ArkWalletEntity` carries a generic `Metadata` JSON column for per-wallet bookkeeping the SDK accumulates over time without requiring a column-add migration per concern. `VtxoSynchronizationService` uses key `vtxo.lastFullPollAt` to persist a cursor that bounds the cold-start catch-up window — on first startup it reads `MIN(per-wallet vtxo.lastFullPollAt)` as the `after` filter so wallets with long history don't refetch every VTXO on every cold start. Routine polls write the same `StartedAt` timestamp to every wallet on success. A failure-then-success sequence cannot advance the cursor past the catch-up window: routine-poll writes are gated until the cold-start catch-up has succeeded at least once. Use `IWalletStorage.SetMetadataValue` for sparse updates so concurrent writers for different concerns (sync, recovery, ...) don't clobber each other.

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

## SQLite: `StoreDateTimeOffsetAsTicks` (opt-in)

Every paged storage query in this SDK (`GetVtxos`, `GetContracts`, `GetIntents`, `GetPayments`, `GetPaymentRequests`, `GetSwaps`) ends with an `ORDER BY` on a `DateTimeOffset` column (`SeenAt`, `CreatedAt`). EF Core's SQLite provider rejects that with:

> SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses.

The reason is that SQLite stores `DateTimeOffset` as TEXT, and the default representation isn't chronologically sortable when offsets differ. Postgres/MSSQL aren't affected — they sort `DateTimeOffset` natively.

To fix SQLite without touching Postgres/MSSQL behaviour, opt in to a `DateTimeOffset → long` (UTC ticks) value conversion:

```csharp
modelBuilder.ConfigureArkEntities(opts =>
{
    opts.Schema = "ark";
    opts.StoreDateTimeOffsetAsTicks = true;   // SQLite consumers: turn this on
});

// If you use payment tracking, set the same flag there too:
modelBuilder.ConfigureArkPaymentEntities(opts =>
{
    opts.Schema = "ark";
    opts.StoreDateTimeOffsetAsTicks = true;
});
```

When on, every `DateTimeOffset` column in the Ark model is stored as INTEGER (SQLite) / BIGINT (Postgres/MSSQL). `ORDER BY` works natively; indexes still work; on-disk size is unchanged.

### Trade-offs

1. **Schema change.** Stored values switch from TEXT/timestamp to INTEGER/BIGINT. Existing data won't deserialize after flipping the flag.
2. **Offset stripped on read-back.** The conversion stores UTC ticks only — the original `DateTimeOffset.Offset` is dropped (always reads back as `TimeSpan.Zero`). Consumers who need the original zoned moment should leave the flag off and accept the SQLite ORDER BY limitation.

### When to enable

| Provider | Recommendation |
|---|---|
| SQLite — paged queries failing with the ORDER BY error | **Enable.** This is the fix. |
| SQLite — no paged queries (or you tolerate the failure) | Leave off. Default preserves native TEXT storage. |
| PostgreSQL / MSSQL | Leave off. You don't have the bug. Default keeps `timestamptz` / `datetimeoffset` and their native date functions. |

### Migrating existing SQLite data

If you have an existing SQLite DB with TEXT-encoded `DateTimeOffset` columns and you want to flip the flag:

```sql
-- One-off migration: convert each DateTimeOffset column from TEXT to INTEGER ticks.
-- Run BEFORE enabling StoreDateTimeOffsetAsTicks.
UPDATE Vtxos SET SeenAt = CAST((julianday(SeenAt) - 1721425.5) * 864000000000 AS INTEGER);
-- repeat for other DateTimeOffset columns (CreatedAt, etc.)
```

> **Requires SQLite ≥ 3.38.0.** Older versions of `julianday()` silently ignore the timezone offset on TEXT inputs (treating local times as UTC and producing wrong ticks). If your runtime ships an older SQLite, prefer the wipe-and-rebuild path below or normalize values to `…Z` before running the UPDATE.

Or — for local caches where data isn't load-bearing — delete the SQLite file and let `EnsureCreated` rebuild the schema with INTEGER columns.

For tests and other contexts where you're starting from scratch, just set the flag in `OnModelCreating` and run `EnsureCreated` — no migration needed.

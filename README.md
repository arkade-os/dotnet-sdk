# NArk .NET SDK

A .NET SDK for building applications on [Arkade](https://arkadeos.com) — a Bitcoin virtual execution layer that enables instant, low-cost, programmable off-chain transactions using virtual UTXOs (VTXOs).

[![NuGet](https://img.shields.io/nuget/v/NArk.svg)](https://www.nuget.org/packages/NArk)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Packages

| Package | Description |
|---------|-------------|
| **NArk.Abstractions** | Interfaces and domain types (`IVtxoStorage`, `IContractStorage`, `IWalletProvider`, `ArkCoin`, `ArkVtxo`, etc.) |
| **NArk.Core** | Core services: spending, batch management, VTXO sync, sweeping, wallet infrastructure, gRPC transport |
| **NArk.Swaps** | Multi-provider swap framework with pluggable providers ([Boltz](https://boltz.exchange) shipped; route-based architecture for adding others) |
| **NArk.Storage.EfCore** | Entity Framework Core storage implementations (provider-agnostic — works with PostgreSQL, SQLite, etc.) |
| **NArk** | Meta-package that pulls in `NArk.Core` + `NArk.Swaps` |

## Quick Start

### Install

```bash
dotnet add package NArk                    # Core + Swaps
dotnet add package NArk.Storage.EfCore     # EF Core persistence
```

### Minimal Setup with Generic Host

```csharp
using NArk.Hosting;
using NArk.Core.Wallet;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Hosting;

var builder = Host.CreateDefaultBuilder(args)
    .AddArk()
    .WithVtxoStorage<EfCoreVtxoStorage>()
    .WithContractStorage<EfCoreContractStorage>()
    .WithIntentStorage<EfCoreIntentStorage>()
    .WithWalletProvider<DefaultWalletProvider>()
    .WithSafetyService<YourSafetyService>()
    .WithBlockchain<NBXplorerBlockchain>()
    .OnMainnet()
    .EnableSwaps();

// Register your DbContext and EF Core storage
builder.ConfigureServices((_, services) =>
{
    services.AddDbContextFactory<YourDbContext>(opts =>
        opts.UseNpgsql(connectionString));

    services.AddArkEfCoreStorage<YourDbContext>();
});

var app = builder.Build();
await app.RunAsync();
```

### Setup with IServiceCollection (plugin/non-host scenarios)

```csharp
using NArk.Hosting;
using NArk.Core.Wallet;
using NArk.Storage.EfCore.Hosting;

services.AddArkCoreServices();
services.AddArkNetwork(ArkNetworkConfig.Mainnet);
services.AddArkSwapServices();

services.AddDbContextFactory<YourDbContext>(opts =>
    opts.UseNpgsql(connectionString));

services.AddArkEfCoreStorage<YourDbContext>();

// Register remaining required services
services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
services.AddSingleton<ISafetyService, YourSafetyService>();

// Pick the blockchain backend you have a client for. Each helper registers
// a single IBitcoinBlockchain that handles chain time, UTXO lookup at a
// boarding address, broadcast, package broadcast, tx status and fee
// estimation. Last registration wins, so you can swap in a custom impl
// after the helper if you want to override one method.
services.AddNBXplorerBlockchain(network, new Uri("http://localhost:32838"));
// or: services.AddEsploraBlockchain(new Uri("https://mempool.space/api/"));
// or: services.AddRpcBlockchain(rpcClient);  // UTXO lookup not supported
```

## Architecture

```
NArk (meta-package)
 ├── NArk.Core
 │    ├── Services (spending, batches, VTXO sync, sweeping, intents)
 │    ├── Wallet (WalletFactory, signers, address providers)
 │    ├── Hosting (DI extensions, ArkApplicationBuilder)
 │    └── Transport (gRPC client for Arkade server communication)
 │
 ├── NArk.Swaps
 │    ├── Abstractions (ISwapProvider, SwapRoute, SwapAsset)
 │    ├── Boltz provider (submarine, reverse & chain swaps)
 │    └── SwapsManagementService (multi-provider router)
 │
 └── NArk.Abstractions
      ├── Domain types (ArkCoin, ArkVtxo, ArkContract, ArkAddress, etc.)
      ├── Storage interfaces (IVtxoStorage, IContractStorage, IIntentStorage)
      └── Wallet interfaces (IWalletProvider, IArkadeWalletSigner)

NArk.Storage.EfCore (optional, provider-agnostic persistence)
 ├── EF Core entity mappings
 ├── Storage implementations
 └── DI extension: AddArkEfCoreStorage<TDbContext>()
```

## Wallet Management

The SDK supports two wallet types:

**HD Wallets** — BIP-39 mnemonic with BIP-86 taproot derivation (`m/86'/cointype'/0'`):

```csharp
var serverInfo = await transport.GetServerInfoAsync();
var wallet = await WalletFactory.CreateWallet(
    "abandon abandon abandon ... about",  // BIP-39 mnemonic
    destination: null,
    serverInfo);
// wallet.WalletType == WalletType.HD
```

**Single-Key Wallets** — nostr `nsec` format (Bech32-encoded secp256k1 key):

```csharp
var wallet = await WalletFactory.CreateWallet(
    "nsec1...",
    destination: null,
    serverInfo);
// wallet.WalletType == WalletType.SingleKey
```

Save and load wallets through `IWalletStorage`:

```csharp
await walletStorage.SaveWallet(wallet);
var loaded = await walletStorage.LoadWallet(wallet.Id);
var all = await walletStorage.LoadAllWallets();
```

## Spending

Use `ISpendingService` to send Arkade transactions:

```csharp
// Automatic coin selection
var txId = await spendingService.Spend(
    walletId,
    outputs: [new ArkTxOut(recipientAddress, Money.Satoshis(10_000))]);

// Manual coin selection
var coins = await spendingService.GetAvailableCoins(walletId);
var txId = await spendingService.Spend(
    walletId,
    inputs: coins.Take(2).ToArray(),
    outputs: [new ArkTxOut(recipientAddress, Money.Satoshis(5_000))]);
```

## Assets

The SDK supports issuing, transferring, and burning assets on Arkade. Assets are encoded as `AssetGroup` entries inside an OP_RETURN output (an "asset packet") attached to each Arkade transaction. The asset ID is derived from `{txid, groupIndex}` after submission.

Asset packets are **deterministic**: `AssetPacketBuilder` emits groups in a stable order (by asset id, then group index) regardless of input order, so the same logical transaction always serializes to identical bytes. This matches the ordering used by the other Arkade SDKs (ts-sdk / rust-sdk) and makes packets reproducible and cross-SDK fixture-comparable.

### Issuance

Use `IAssetManager` to create new assets:

```csharp
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1000));

// result.AssetId  — the unique asset identifier
// result.ArkTxId  — the Arkade transaction that created it
```

Issue with metadata:

```csharp
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(
        Amount: 1000,
        Metadata: new Dictionary<string, string>
        {
            { "name", "My Token" },
            { "ticker", "MTK" },
            { "decimals", "8" }
        }));
```

### Controlled Issuance & Reissuance

A control asset acts as a minting key — only the holder can issue more supply:

```csharp
// Issue a control asset (amount=1, acts as the minting authority)
var control = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1));

// Issue a token controlled by that asset
var token = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1000, ControlAssetId: control.AssetId));

// Reissue more supply later (requires holding the control asset)
await assetManager.ReissueAsync(walletId,
    new ReissuanceParams(control.AssetId, Amount: 500));
```

### Transfer

Asset transfers use the standard `SpendingService.Spend()` with `ArkTxOut.Assets`:

```csharp
await spendingService.Spend(walletId,
[
    new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, recipientAddress)
    {
        Assets = [new ArkTxOutAsset(assetId, 400)]
    }
]);
// Automatic coin selection handles BTC fees and asset change.
// Sender retains remaining units (e.g. 600 of 1000) as asset change.
```

### Burn

Reduce the circulating supply of an asset:

```csharp
await assetManager.BurnAsync(walletId,
    new BurnParams(assetId, Amount: 400));
// Remaining 600 units are returned as change
```

### Querying Assets

Check asset balances from local VTXO storage:

```csharp
var coins = await spendingService.GetAvailableCoins(walletId);
foreach (var coin in coins.Where(c => c.Assets is { Count: > 0 }))
{
    foreach (var asset in coin.Assets!)
        Console.WriteLine($"Asset {asset.AssetId}: {asset.Amount} units");
}
```

Query asset details from the Arkade server:

```csharp
var details = await transport.GetAssetDetailsAsync(assetId);
// details.Supply — total circulating supply
// details.AssetId — the asset identifier
// details.Metadata — key-value metadata (if set during issuance)
```

## Delegation

Delegation solves the VTXO liveness problem — VTXOs expire if not refreshed. A delegate service (e.g., [Fulmine](https://github.com/ArkLabsHQ/fulmine)) participates in batch rounds on your behalf, rolling VTXOs over before expiry.

### Automated Delegation

When `AddArkDelegation` is configured, the SDK automatically:
1. **Derives delegate contracts** — HD wallets produce `ArkDelegateContract` instead of `ArkPaymentContract` for Receive/SendToSelf operations
2. **Auto-delegates VTXOs** — when VTXOs arrive at delegate contract addresses, the SDK builds partially signed intent + ACP forfeit txs and sends them to the delegator

```csharp
services.AddArkCoreServices();

// Enable automated delegation (Fulmine delegator gRPC endpoint)
services.AddArkDelegation("http://localhost:7012");

// That's it. HD wallets will now:
// - Derive ArkDelegateContract for new receive/change addresses
// - Auto-delegate incoming VTXOs to the delegator on receipt
// nsec wallets (hashlock/note contracts) are unaffected.
```

The delegate contract has three spending paths:
- **CollaborativePath** (User + Server, 2-of-2) — collaborative spending, same as a regular payment contract
- **DelegatePath** (User + Delegate + Server, 3-of-3) — used by the delegator for ACP forfeit txs
- **ExitPath** (User only, after CSV delay) — unilateral recovery

### Manual Delegation

For fine-grained control, you can manually construct delegate contracts and delegate VTXOs:

```csharp
// Get delegator info
var info = await delegationService.GetDelegatorInfoAsync();

// Create a delegate contract
var delegateContract = new ArkDelegateContract(
    serverInfo.SignerKey,
    serverInfo.UnilateralExit,
    userKey,
    KeyExtensions.ParseOutputDescriptor(info.Pubkey, network),
    cltvLocktime: new LockTime(currentHeight + 100)); // optional safety window

// Send VTXOs to the delegate contract address
await spendingService.Spend(walletId,
    outputs: [new ArkTxOut(delegateContract.GetArkAddress(), amount)]);

// Delegate to the delegator
await delegationService.DelegateAsync(
    intentMessage: intentJson,
    intentProof: proofPsbtBase64,
    forfeitTxs: forfeitTxHexArray,
    rejectReplace: false);
```

The CLTV locktime is optional — when set, it prevents the delegate from acting before a specific block height, giving the owner a safety window.

### Custom Contract Delegation

The SDK uses an `IDelegationTransformer` pattern to support delegating different contract types. The built-in `DelegateContractDelegationTransformer` handles `ArkDelegateContract` VTXOs and is registered by `AddArkDelegation`. Register additional transformers for other contract types *after* calling `AddArkDelegation`:

```csharp
services.AddArkDelegation("http://localhost:7012");
services.AddTransient<IDelegationTransformer, MyCustomDelegationTransformer>();
```

> Note: `DelegationService` and the default `IDelegationTransformer` are only registered by `AddArkDelegation`. `AddArkCoreServices` alone does not include delegation services.

Each transformer implements:
- `CanDelegate(walletId, contract, delegatePubkey)` — check eligibility
- `GetDelegationScriptBuilders(contract)` — return (intentScript, forfeitScript) for building delegation artifacts

## Collaborative Exits (On-chain)

Move funds from Arkade back to the Bitcoin base layer:

```csharp
var btcTxId = await onchainService.InitiateCollaborativeExit(
    walletId,
    new ArkTxOut(bitcoinAddress, Money.Satoshis(50_000)));
```

## Querying Intents by Proof

Retrieve registered intents by proving ownership of any input coin via a BIP-322-style proof:

```csharp
// Create a signed ownership proof for a coin
var (proof, message) = await IntentProofHelper.CreateIntentOwnershipProofAsync(
    coin, signer, network);

// Query arkd for intents registered with this coin
var intents = await transport.GetIntentsByProofAsync(proof, message);
```

The `IntentProofHelper.CreateBip322Psbt` and `IntentProofHelper.SignBip322Proof` building blocks are also available separately for delegation and other proof flows.

## Boarding (On-chain → Arkade)

Boarding lets users move on-chain Bitcoin UTXOs into the Arkade VTXO tree. The user deposits BTC to a boarding address (a P2TR output with a collaborative spend path and a CSV-locked unilateral exit). Once confirmed, the boarding UTXO is automatically picked up by the intent/batch pipeline — no manual intervention needed.

### 1. Derive a Boarding Address

```csharp
var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
    walletId,
    NextContractPurpose.Boarding);

// Get the on-chain P2TR (bc1p...) address for the user to deposit BTC to
var onchainAddress = boardingContract.GetOnchainAddress(network);
```

### 2. Sync On-chain UTXOs

`BoardingUtxoSyncService` polls a blockchain indexer for confirmed UTXOs at your boarding addresses and upserts them into VTXO storage. It depends on `IBitcoinBlockchain` — register one of the built-in backends:

```csharp
// Option A: Esplora (mempool.space, Chopsticks, etc.)
// ArkNetworkConfig.{Mainnet,Mutinynet,Regtest} carry per-network
// endpoint defaults that mirror the canonical Arkade ts-sdk:
//
//   Network    EsploraUri                                 ElectrumWsUri                              ElectrumTcpHost
//   Mainnet    https://mempool.arkade.sh/api              wss://electrum.arkade.sh                   electrum.arkade.sh
//   Mutinynet  https://mempool.mutinynet.arkade.sh/api    wss://electrum.mutinynet.arkade.sh         electrum.mutinynet.arkade.sh
//   Regtest    http://localhost:3000                      ws://localhost:50003                       localhost
//
// ElectrumWsUri is the websocket URL (full scheme://host[:port]); the
// public Ark Labs Fulcrum instances also expose plain TCP (port 50001),
// TCP+TLS (50002), and WSS (50004). ElectrumTcpHost is hostname-only —
// the caller picks the port.
services.AddEsploraBlockchain(new Uri(ArkNetworkConfig.Mainnet.EsploraUri!));
// or pass your own URL: services.AddEsploraBlockchain(new Uri("https://mempool.space/api/"));

// Option B: NBXplorer (BTCPay Server, self-hosted)
services.AddNBXplorerBlockchain(network, new Uri("http://localhost:32838"));

// Option C: Bitcoin Core RPC (does NOT support UTXO lookup — chain time
// + broadcast + fee estimation only; pair with one of the above if you
// also need boarding sync)
services.AddRpcBlockchain(rpcClient);

services.AddSingleton<BoardingUtxoSyncService>();

// Register the poll service — automatically polls every 30s
// when unspent boarding VTXOs exist
services.AddSingleton<BoardingUtxoPollService>();
services.AddHostedService(sp => sp.GetRequiredService<BoardingUtxoPollService>());
```

The `BoardingUtxoPollService` automatically checks for unspent boarding VTXOs every 30 seconds and syncs confirmation state changes. It complements event-driven sync (e.g., NBXplorer transaction events) to catch missed events during provider reconnects or block confirmations.

Once a boarding UTXO is synced and confirmed, the SDK's `IntentGenerationService` automatically creates an intent for it. The next batch moves it into the VTXO tree.

### 3. Handle Expired Boarding UTXOs (Optional)

If a boarding UTXO isn't batched before its CSV timelock expires, `OnchainSweepService` detects it. Register a custom `IOnchainSweepHandler` to control what happens:

```csharp
public class MySweepHandler : IOnchainSweepHandler
{
    public async Task<bool> HandleExpiredUtxoAsync(
        string walletId, ArkVtxo vtxo, ArkContractEntity contract,
        CancellationToken ct)
    {
        // Sweep to a new boarding address, cold storage, etc.
        return true; // true = handled, false = fall back to default
    }
}

services.AddSingleton<IOnchainSweepHandler, MySweepHandler>();
```

Then call `SweepExpiredUtxosAsync()` periodically:

```csharp
var sweepService = new OnchainSweepService(
    vtxoStorage, contractStorage, chainTimeProvider,
    contractService, walletProvider, sweepHandler);

await sweepService.SweepExpiredUtxosAsync(ct);
```

## Unilateral Exit

When the Ark server goes offline or becomes uncooperative, users can **unilaterally exit** by broadcasting the chain of virtual transactions from commitment tx to their VTXO leaf, waiting a CSV timelock, then claiming funds on-chain.

### Setup

```csharp
services.AddUnilateralExit(
    configureVirtualTx: opts =>
    {
        opts.DefaultMode = VirtualTxMode.Lite;  // Default: txids + expiry only; hex fetched on exit
        opts.MinExitWorthAmount = 1000;         // Skip tiny VTXOs not worth exiting
    },
    configureWatchtower: opts =>
    {
        opts.PollInterval = TimeSpan.FromSeconds(60);
    });

// Wire the single IBitcoinBlockchain (chain time + UTXO lookup + broadcast +
// package broadcast + tx status + fee estimation) in one call. Pick the
// backend you have a client for: AddNBXplorerBlockchain, AddEsploraBlockchain,
// or AddRpcBlockchain. RPC does not implement UTXO lookup (Bitcoin Core has
// no native address index). Last registration wins — register a custom
// impl afterwards to swap the whole backend.
services.AddNBXplorerBlockchain(explorerClient);

// Opt in to durable EF Core storage for sessions + chains (mirrors the
// payment-tracking entity opt-in). Skip if you'd rather use in-memory
// storage or the stateless one-shot API below.
modelBuilder.ConfigureArkExitEntities();

// Opt in to background pre-fetching of chain data on every VTXO arrival
// (subscribes to IVtxoStorage.VtxosChanged). Without this, chains are
// fetched lazily when StartExitAsync is invoked.
services.AddVirtualTxAutoFetch();

// Optional: run watchtower as background service
services.AddExitWatchtowerBackgroundService();
```

### Starting an Exit

```csharp
var exitService = serviceProvider.GetRequiredService<UnilateralExitService>();

// Exit specific VTXOs
var sessions = await exitService.StartExitAsync(
    walletId,
    vtxoOutpoints,
    claimAddress,     // Bitcoin address to receive claimed funds
    cancellationToken);

// Or exit all VTXOs in a wallet
var sessions = await exitService.StartExitForWalletAsync(
    walletId, claimAddress, cancellationToken);
```

### Progressing Exits

Call `ProgressExitsAsync` periodically to advance exit sessions through their state machine:

```csharp
// Broadcasting → AwaitingCsvDelay → Claimable → Claiming → Completed
await exitService.ProgressExitsAsync(cancellationToken);
```

The exit watchtower background service does this automatically if registered.

### Virtual Tx Storage Modes

- **Lite mode (default)**: Stores only txids + expiry. Fetches hex on demand when exit is actually started (saves storage, slower exit start). Right default for most wallets — the common case never exits unilaterally.
- **Full mode**: Fetches and stores raw tx hex on VTXO receive. Ready for instant exit without any indexer round-trip. Opt in via `opts.DefaultMode = VirtualTxMode.Full` when offline-exit capability is a hard requirement.

### No-Storage Modes

Two ways to use unilateral exit without paying the EF Core schema cost:

**1. In-memory storage** — same code paths as the durable flow (idempotent re-invocation, watchtower visibility) but state is held in `ConcurrentDictionary`s and lost on process restart. Right for recovery tooling, plugins, or ephemeral wallets that don't need cross-restart resume.

```csharp
services.AddUnilateralExit();
services.AddInMemoryExitStorage();  // registers InMemoryExitSessionStorage + InMemoryVirtualTxStorage
// Don't call ConfigureArkExitEntities() — no SQL tables needed
```

**2. Stateless one-shot API** — `UnilateralExitService.BroadcastExitChainAsync` + `ClaimMaturedExitAsync` skip both `IExitSessionStorage` and `IVirtualTxStorage` entirely. The SDK persists nothing exit-specific; the caller saves the returned `ExitPlan` record however they want and feeds it back to claim once the CSV timelock matures.

```csharp
// Broadcast the chain now — no SDK persistence
var plan = await exitService.BroadcastExitChainAsync(
    walletId, vtxoOutpoint, claimAddress, ct);

// ... persist `plan` somewhere (a JSON blob, a settings entry, etc.) ...

// Later — once leaf-tx confirms + CSV matures:
var claimTxid = await exitService.ClaimMaturedExitAsync(plan, ct);
if (claimTxid is null)
{
    // CSV not yet matured; try again later
}
```

Trade-off vs. the stateful path: no idempotency (a second `BroadcastExitChainAsync` will re-broadcast), no automatic watchtower progression. The caller owns persistence and time-keeping in their own format.

Virtual tx data is automatically pruned when VTXOs are spent. Sibling VTXOs sharing internal tree nodes naturally deduplicate — shared nodes are only cleaned up when no VTXO references them.

## Contracts

Derive receiving addresses and manage contracts:

```csharp
// Derive a new receive contract (generates a new Arkade address)
var contract = await contractService.DeriveContract(
    walletId,
    NextContractPurpose.Receive);

// The contract's script can be converted to an ArkAddress for display
```

## HD Wallet Recovery

When importing an HD wallet from its mnemonic, the SDK has no record of contracts the previous instance derived. `HdWalletRecoveryService` rebuilds that state by sweeping derivation indices via gap-limit and asking each registered `IContractDiscoveryProvider` whether it ever saw activity at that index.

The default providers ship with the SDK:

- `IndexerVtxoDiscoveryProvider` (`AddArkCoreServices`) — asks arkd's indexer for VTXOs at the index's payment script.
- `BoardingUtxoDiscoveryProvider` (`AddArkCoreServices`, opt-in via registering an `IBitcoinBlockchain` whose `GetUtxosAsync` is implemented — NBXplorer or Esplora) — asks for historical UTXOs at the index's boarding address.
- `BoltzSwapDiscoveryProvider` (`AddArkSwapServices`) — asks Boltz `/v2/swap/restore` whether the index's user pubkey ever participated in a swap.

```csharp
var recovery = serviceProvider.GetRequiredService<HdWalletRecoveryService>();

var report = await recovery.ScanAsync(walletId);
// or with options:
var deepReport = await recovery.ScanAsync(walletId, new RecoveryOptions(GapLimit: 50));

Console.WriteLine($"Highest used index: {report.HighestUsedIndex}");
Console.WriteLine($"Discovered {report.DiscoveredContracts.Count} contract(s)");
```

Custom discovery sources are added by implementing `IContractDiscoveryProvider` and registering it in DI; the orchestrator picks them up automatically. See [docs/articles/recovery.md](docs/articles/recovery.md) for the full API and tuning guidance.

## Pending Arkade Transaction Recovery

Arkade off-chain transactions are a two-phase **Submit → Finalize** flow. If the process crashes between phases, the server holds the inputs as in-flight and only allows the original pending tx to be finalized — without recovery, those coins are stuck.

`PendingArkTransactionRecoveryService` reconciles this on every host startup. It pulls the server's view of pending transactions for each wallet, signs the checkpoint PSBTs locally, and finalizes them. It's registered automatically by `AddArkCoreServices` and wired into `ArkHostedLifecycle`, so the hands-off path requires no extra setup.

For deterministic timing (e.g. immediately after a user unlock) call it explicitly per-wallet:

```csharp
var recovery = serviceProvider.GetRequiredService<PendingArkTransactionRecoveryService>();

var finalizedTxIds = await recovery.FinalizePendingArkTransactionsAsync(walletId, ct);
foreach (var txId in finalizedTxIds)
    Console.WriteLine($"Recovered & finalized pending tx {txId}");
```

Per-tx failures are logged + raised on `RecoveryFailed` and the loop continues — one bad pending tx never blocks the rest, and the next host start retries any leftovers. Subscribe to surface a banner or telemetry:

```csharp
recovery.RecoveryFailed += (_, e) =>
    Logger.LogWarning("Recovery failed for {ArkTxId} on {WalletId}: {Error}",
        e.ArkTxId, e.WalletId, e.Exception.Message);
```

See [docs/articles/pending-tx-recovery.md](docs/articles/pending-tx-recovery.md) for the full flow, sequence diagram, and edge cases.

## EF Core Storage

`NArk.Storage.EfCore` provides ready-made storage implementations. It is **provider-agnostic** — no dependency on Npgsql or any specific database driver.

### DbContext Setup

In your `DbContext.OnModelCreating`, call `ConfigureArkEntities`:

```csharp
public class MyDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureArkEntities(opts =>
        {
            opts.Schema = "ark";           // default
            // opts.WalletsTable = "Wallets";   // all table names configurable
        });
    }
}
```

### Storage Options

`ArkStorageOptions` controls schema, table names, and provider-specific behavior:

```csharp
services.AddArkEfCoreStorage<MyDbContext>(opts =>
{
    opts.Schema = "my_schema";

    // PostgreSQL-specific text search on contract metadata
    opts.ContractSearchProvider = (query, searchText) =>
        query.Where(c => EF.Functions.ILike(c.Metadata, $"%{searchText}%"));
});
```

### SQLite consumers: enable `StoreDateTimeOffsetAsTicks`

EF Core's SQLite provider rejects `ORDER BY` on `DateTimeOffset` columns, which breaks every paged query in this SDK (`GetVtxos`, `GetContracts`, `GetIntents`, …). Set `StoreDateTimeOffsetAsTicks = true` in `ConfigureArkEntities` (and `ConfigureArkPaymentEntities` if you use payment tracking) to store these columns as INTEGER ticks instead — `ORDER BY` then works natively.

```csharp
modelBuilder.ConfigureArkEntities(opts => opts.StoreDateTimeOffsetAsTicks = true);
```

Off by default to preserve native column types for Postgres/MSSQL consumers. Trade-off: the round-trip strips the original timezone offset (reads back as UTC). See [docs/articles/storage.md](docs/articles/storage.md#sqlite-storedatetimeoffsetastick-opt-in) for migration paths and details.

### Entities

| Entity | Table | Primary Key |
|--------|-------|-------------|
| `ArkWalletEntity` | `Wallets` | `Id` |
| `ArkWalletContractEntity` | `WalletContracts` | `(Script, WalletId)` |
| `VtxoEntity` | `Vtxos` | `(TransactionId, TransactionOutputIndex)` |
| `ArkIntentEntity` | `Intents` | `IntentTxId` |
| `ArkIntentVtxoEntity` | `IntentVtxos` | `(IntentTxId, VtxoTransactionId, VtxoTransactionOutputIndex)` |
| `ArkSwapEntity` | `Swaps` | `(SwapId, WalletId)` |

Payment-tracking entities (`ArkPaymentEntity`, `ArkPaymentRequestEntity`) are opt-in — see [Payment Repository](#payment-repository) below.

## Payment Repository

The SDK includes an **opt-in** payment repository for tracking end-to-end payments — both outbound (sends) and inbound (payment requests). This replaces the need for consumers to build their own payment-to-protocol linkage.

### Opt-In Setup

Payment tracking is not wired up by `AddArkEfCoreStorage` / `ConfigureArkEntities` — consumers that don't need it carry no extra schema or services. To enable it, call both the DI and model extensions:

```csharp
// OnModelCreating — alongside ConfigureArkEntities
modelBuilder.ConfigureArkEntities(opts => opts.Schema = "ark");
modelBuilder.ConfigureArkPaymentEntities(opts => opts.Schema = "ark");

// DI — alongside AddArkEfCoreStorage
services.AddArkEfCoreStorage<MyDbContext>();
services.AddArkPaymentTracking();
```

`AddArkPaymentTracking` registers `IPaymentStorage`, `IPaymentRequestStorage`, and the `PaymentTrackingService` (as an `IHostedService`, so its event subscriptions activate on startup). After calling `ConfigureArkPaymentEntities`, add the corresponding EF Core migration so the `Payments` and `PaymentRequests` tables are created.

### Outbound Payments (`ArkPayment`)

Track a payment you're sending, linked to the protocol object that proves it:

```csharp
var payment = new ArkPayment(
    PaymentId: Guid.NewGuid().ToString(),
    WalletId: walletId,
    Recipient: "tark1q...",
    Amount: 50_000,
    Method: ArkPaymentMethod.ArkSend,
    Status: ArkPaymentStatus.Pending,
    FailReason: null,
    CreatedAt: DateTimeOffset.UtcNow,
    CompletedAt: null)
{
    IntentTxId = intentTxId // links to the Arkade intent
};

await paymentStorage.SavePayment(payment);

// Query payments
var pending = await paymentStorage.GetPayments(
    walletIds: [walletId],
    statuses: [ArkPaymentStatus.Pending]);
```

Payment methods: `ArkSend`, `CollaborativeExit`, `SubmarineSwap`, `ChainSwap`.
Proof fields: `IntentTxId` (Arkade sends), `SwapId` (swaps), `OnchainTxId` (collab exits).

### Inbound Payment Requests (`ArkPaymentRequest`)

Generate a payment request with multiple payment options:

```csharp
var request = new ArkPaymentRequest(
    RequestId: Guid.NewGuid().ToString(),
    WalletId: walletId,
    Amount: 100_000,             // null = any amount (donation-style)
    Description: "Order #1234",
    Status: ArkPaymentRequestStatus.Pending,
    ReceivedAmount: 0,
    CreatedAt: DateTimeOffset.UtcNow,
    ExpiresAt: DateTimeOffset.UtcNow.AddHours(1))
{
    ArkAddress = "tark1q...",
    BoardingAddress = "bcrt1p...",
    LightningInvoice = "lnbcrt...",
    ContractScripts = [arkScript, boardingScript], // scripts to watch
    SwapId = reverseSwapId                          // if Lightning enabled
};

await paymentRequestStorage.SavePaymentRequest(request);

// Look up by script (for matching incoming VTXOs)
var matched = await paymentRequestStorage.GetPaymentRequestByScript(vtxoScript);
```

### Automatic Status Tracking (`PaymentTrackingService`)

The `PaymentTrackingService` subscribes to `VtxosChanged`, `IntentChanged`, and `SwapsChanged` events and automatically updates payment statuses:

- **Outbound**: When an intent succeeds/fails or a swap settles/fails, the linked `ArkPayment` moves to `Completed` or `Failed`.
- **Inbound**: When a VTXO arrives on a watched contract script, the `ArkPaymentRequest` accumulates `ReceivedAmount` and transitions to `Paid` (or `PartiallyPaid` for fixed-amount requests). Overpayment is tracked in the `Overpayment` property.

It is registered by `AddArkPaymentTracking()` (see [Opt-In Setup](#opt-in-setup) above) and runs as an `IHostedService`, so its event subscriptions activate automatically on application startup — no manual resolution needed.

### Fulfillment Rules

- **Any-amount requests** (`Amount = null`): `Paid` immediately on first funds received.
- **Fixed-amount requests**: `Paid` when `ReceivedAmount >= Amount`. No underpayment tolerance.
- **Overpayment**: Tracked via `ArkPaymentRequest.Overpayment` (sats above the target). Status is still `Paid`.
- **Expiration**: Handled externally (timer/cron), not by the tracking service.

## Networks

Pre-configured network environments:

```csharp
// Fluent builder
builder.AddArk().OnMainnet();
builder.AddArk().OnMutinynet();
builder.AddArk().OnRegtest();
builder.AddArk().OnCustomGrpcArk("http://my-ark-server:7070");

// IServiceCollection
services.AddArkNetwork(ArkNetworkConfig.Mainnet);
services.AddArkNetwork(new ArkNetworkConfig(
    ArkUri: "http://my-ark-server:7070",
    BoltzUri: "http://my-boltz:9069/"));
```

## Swaps

The swap framework is **multi-provider** — swap providers are pluggable via DI and the `SwapsManagementService` routes operations to the right provider based on the requested asset pair.

### Concepts

A **swap route** is a directional asset pair:

```csharp
// Route = source asset → destination asset
var route = new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc);  // Lightning → Ark
var route = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);    // Ark → BTC on-chain

// Arkade-issued assets
var myToken = SwapAsset.ArkAsset("asset1abc...");
```

Each `ISwapProvider` declares which routes it supports. The router resolves the correct provider for a given route automatically.

### Registration

```csharp
// Default: core services + Boltz (backward-compatible)
services.AddArkSwapServices();
```

Or register providers individually:

```csharp
// Core services only (no providers)
services.AddSingleton<SwapsManagementService>();
services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();
services.AddSingleton<IContractTransformer, VHTLCContractTransformer>();

// Pick your providers
services.AddBoltzProvider(opts => opts.BoltzUrl = "https://api.boltz.exchange");
```

### Route Discovery

Query which routes are available across all registered providers:

```csharp
var swaps = serviceProvider.GetRequiredService<SwapsManagementService>();

// All routes from all providers
var routes = await swaps.GetAvailableRoutesAsync(ct);
// e.g. [Lightning→Arkade, Arkade→Lightning, BTC→Arkade, Arkade→BTC, ...]
```

### Pricing

Get limits and quotes — the router picks the right provider:

```csharp
var route = new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc);

var limits = await swaps.GetLimitsAsync(route, ct);
// limits.MinAmount, limits.MaxAmount, limits.FeePercentage, limits.MinerFee

var quote = await swaps.GetQuoteAsync(route, amount: 100_000, ct);
// quote.SourceAmount, quote.DestinationAmount, quote.TotalFees, quote.ExchangeRate
```

### Providers

| Provider | Routes | Features |
|----------|--------|----------|
| **Boltz** | Arkade &harr; Lightning, Arkade &harr; BTC on-chain | Submarine/reverse swaps, chain swaps with renegotiation, MuSig2 cooperative claiming **and refunding** (both BTC and Arkade sides), VHTLC management, WebSocket status updates |

### Recovery (Renegotiation + Cooperative Refund)

When a chain swap can't settle as originally quoted — user funds the lockup with the wrong amount, an LN invoice times out, or Boltz expires the swap — the SDK handles recovery automatically inside `BoltzSwapProvider.PollSwapState`. No manual call is needed.

* **`transaction.lockupFailed`** → asks Boltz for a renegotiated quote via `GET/POST /v2/swap/chain/{id}/quote` and updates `ArkSwap.ExpectedAmount` if Boltz accepts.
* **`swap.expired` / `transaction.failed` / `transaction.refunded`** → cooperative refund: BTC→Arkade refunds the BTC lockup with MuSig2 (`/v2/swap/chain/{id}/refund`); Arkade→BTC refunds the Ark VHTLC via `/v2/swap/chain/{id}/refund/ark`. Marks the swap `Refunded`.
* **`swap.expired` with no funds locked** → marked `Failed` (nothing to recover).

Subscribe to `ISwapStorage.SwapsChanged` to observe transitions. To surface a "recovery available" indicator without committing to a refund, use the read-only inspectors:

```csharp
// Single swap
var info = await swapMgr.InspectSwapRecoveryAsync(walletId, swapId);
if (info.Status == SwapRecoveryStatus.Recoverable)
    Console.WriteLine($"{info.AmountSats} sats stranded — recovery runs automatically");

// Bulk audit (e.g. after wallet restore)
var report = await swapMgr.ScanRecoverableSwapsAsync(walletId);
```

### Implementing a Custom Provider

Implement `ISwapProvider` and register it:

```csharp
public class MySwapProvider : ISwapProvider
{
    public string ProviderId => "myprovider";
    public string DisplayName => "My Swap Provider";

    public bool SupportsRoute(SwapRoute route) =>
        route == new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning);

    public Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct) => ...;
    public Task StartAsync(string walletId, CancellationToken ct) => ...;
    public Task StopAsync(CancellationToken ct) => ...;
    public Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct) => ...;
    public Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct) => ...;
    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Register
services.AddSingleton<ISwapProvider, MySwapProvider>();
```

The `SwapsManagementService` will automatically discover it and route matching requests to it.

## Extensibility Points

The SDK uses a pluggable architecture. Register your implementations for:

| Interface | Purpose | Default |
|-----------|---------|---------|
| `IVtxoStorage` | VTXO persistence | `EfCoreVtxoStorage` |
| `IContractStorage` | Contract persistence | `EfCoreContractStorage` |
| `IIntentStorage` | Intent persistence | `EfCoreIntentStorage` |
| `ISwapStorage` | Swap persistence | `EfCoreSwapStorage` |
| `ISwapProvider` | Swap provider (route-based) | `BoltzSwapProvider` |
| `IWalletStorage` | Wallet persistence | `EfCoreWalletStorage` |
| `IWalletProvider` | Wallet signer/address resolution | `DefaultWalletProvider` |
| `ISafetyService` | Distributed locking | *Must implement* |
| `IBitcoinBlockchain` | Chain time, UTXO lookup, broadcast, fee estimation | `NBXplorerBlockchain` / `EsploraBlockchain` / `RpcBlockchain` |
| `IFeeEstimator` | Transaction fee estimation | `DefaultFeeEstimator` |
| `ICoinSelector` | UTXO selection strategy | `DefaultCoinSelector` |
| `ISweepPolicy` | VTXO consolidation rules | Register zero or more |
| `IContractTransformer` | Custom contract &rarr; coin transforms | Register zero or more |
| `IEventHandler<T>` | React to batch/sweep/spend events | Register zero or more |

## Local Development

The SDK uses [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) for local orchestration with Docker containers (arkd, Bitcoin Core, Boltz, etc.):

```bash
cd NArk.AppHost
dotnet run
```

### Running Tests

```bash
# Unit tests
dotnet test NArk.Tests

# End-to-end tests (requires Docker)
dotnet test NArk.Tests.End2End
```

## License

[MIT](LICENSE)

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
    .WithTimeProvider<YourChainTimeProvider>()
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
services.AddSingleton<IChainTimeProvider, YourChainTimeProvider>();
```

## Architecture

```
NArk (meta-package)
 ├── NArk.Core
 │    ├── Services (spending, batches, VTXO sync, sweeping, intents)
 │    ├── Wallet (WalletFactory, signers, address providers)
 │    ├── Hosting (DI extensions, ArkApplicationBuilder)
 │    └── Transport (gRPC client for Ark server communication)
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

Use `ISpendingService` to send Ark transactions:

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

The SDK supports issuing, transferring, and burning assets on Ark. Assets are encoded as `AssetGroup` entries inside an OP_RETURN output (an "asset packet") attached to each Ark transaction. The asset ID is derived from `{txid, groupIndex}` after submission.

### Issuance

Use `IAssetManager` to create new assets:

```csharp
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1000));

// result.AssetId  — the unique asset identifier
// result.ArkTxId  — the Ark transaction that created it
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

Query asset details from the Ark server:

```csharp
var details = await transport.GetAssetDetailsAsync(assetId);
// details.Supply — total circulating supply
// details.AssetId — the asset identifier
// details.Metadata — key-value metadata (if set during issuance)
```

## Collaborative Exits (On-chain)

Move funds from Ark back to the Bitcoin base layer:

```csharp
var btcTxId = await onchainService.InitiateCollaborativeExit(
    walletId,
    new ArkTxOut(bitcoinAddress, Money.Satoshis(50_000)));
```

## Contracts

Derive receiving addresses and manage contracts:

```csharp
// Derive a new receive contract (generates a new Ark address)
var contract = await contractService.DeriveContract(
    walletId,
    NextContractPurpose.Receive);

// The contract's script can be converted to an ArkAddress for display
```

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

### Entities

| Entity | Table | Primary Key |
|--------|-------|-------------|
| `ArkWalletEntity` | `Wallets` | `Id` |
| `ArkWalletContractEntity` | `WalletContracts` | `(Script, WalletId)` |
| `VtxoEntity` | `Vtxos` | `(TransactionId, TransactionOutputIndex)` |
| `ArkIntentEntity` | `Intents` | `IntentTxId` |
| `ArkIntentVtxoEntity` | `IntentVtxos` | `(IntentTxId, VtxoTransactionId, VtxoTransactionOutputIndex)` |
| `ArkSwapEntity` | `Swaps` | `(SwapId, WalletId)` |

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

// EVM tokens use contract addresses as asset IDs
var usdcOnEth = SwapAsset.Erc20(SwapNetwork.EvmEthereum, "0xa0b8...");
var route = new SwapRoute(SwapAsset.ArkBtc, usdcOnEth);               // Ark → USDC on Ethereum

// Ark-issued assets
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
// e.g. [Lightning→Ark, Ark→Lightning, BTC→Ark, Ark→BTC, ...]
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
| **Boltz** | Ark &harr; Lightning, Ark &harr; BTC on-chain | Submarine/reverse swaps, chain swaps, MuSig2 cooperative claiming, VHTLC management, WebSocket status updates |

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
| `IChainTimeProvider` | Current blockchain height/time | *Must implement* |
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

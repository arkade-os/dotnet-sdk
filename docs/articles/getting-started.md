# Getting Started

## Install

```bash
dotnet add package NArk                    # Core + Swaps
dotnet add package NArk.Storage.EfCore     # EF Core persistence (optional)
```

## Minimal Setup with Generic Host

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

builder.ConfigureServices((_, services) =>
{
    services.AddDbContextFactory<YourDbContext>(opts =>
        opts.UseNpgsql(connectionString));

    services.AddArkEfCoreStorage<YourDbContext>();
});

var app = builder.Build();
await app.RunAsync();
```

## Setup with IServiceCollection

For plugin or non-host scenarios (e.g., BTCPay Server plugins):

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

services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
services.AddSingleton<ISafetyService, YourSafetyService>();
services.AddSingleton<IChainTimeProvider, YourChainTimeProvider>();
```

## Networks

```csharp
// Pre-configured networks
.OnMainnet()      // mainnet arkd
.OnMutinynet()    // Mutinynet testnet
.OnRegtest()      // local regtest

// Custom arkd endpoint
.OnCustomGrpcArk("https://your-arkd.example.com")
```

Or via `IServiceCollection`:

```csharp
services.AddArkNetwork(ArkNetworkConfig.Mainnet);
services.AddArkNetwork(new ArkNetworkConfig(
    ArkUri: "http://my-ark-server:7070",
    BoltzUri: "http://my-boltz:9069/"));
```

## Opt-In Features

Several features are opt-in and must be wired up explicitly:

```csharp
// Automated delegation (requires a Fulmine delegator endpoint)
services.AddArkDelegation("http://localhost:7012");

// Payment-tracking storage (opt-in over AddArkEfCoreStorage)
services.AddArkPaymentTracking();
// Also call modelBuilder.ConfigureArkPaymentEntities() in OnModelCreating.
```

See [Storage](storage.md) for details on payment tracking, and the SDK README for delegation.

## Next Steps

- [Architecture](architecture.md) — SDK layering and extensibility
- [Wallets](wallets.md) — create and manage HD or SingleKey wallets
- [Spending](spending.md) — send payments with automatic coin selection
- [Storage](storage.md) — EF Core persistence and opt-in payment tracking

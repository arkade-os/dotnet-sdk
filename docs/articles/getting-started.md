# Getting Started

## Install

```bash
dotnet add package NArk                    # Core + Swaps
dotnet add package NArk.Storage.EfCore     # EF Core persistence
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

// Custom server
.OnCustom(new Uri("https://your-arkd.example.com"))
```

## Next Steps

- [Architecture](architecture.md) — understand SDK layering and extensibility
- [Wallets](wallets.md) — create and manage HD or SingleKey wallets
- [Spending](spending.md) — send payments with automatic coin selection

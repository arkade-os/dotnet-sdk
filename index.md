---
_layout: landing
---

# NArk .NET SDK

> The official .NET SDK for the [Ark](https://ark-protocol.org) protocol — build self-custodial, off-chain Bitcoin applications.

NArk provides everything you need to integrate Ark into .NET applications: wallet management, VTXO lifecycle, intent-based payments, asset support, Lightning swaps via Boltz, and pluggable storage.

## Packages

| Package | Description |
|---|---|
| **[NArk](https://www.nuget.org/packages/NArk)** | Meta-package — pulls in Core + Swaps |
| **[NArk.Abstractions](https://www.nuget.org/packages/NArk.Abstractions)** | Interfaces and domain types |
| **[NArk.Core](https://www.nuget.org/packages/NArk.Core)** | Wallet, VTXO, intent, batch, and asset logic |
| **[NArk.Swaps](https://www.nuget.org/packages/NArk.Swaps)** | Boltz submarine/reverse/chain swap client |
| **[NArk.Storage.EfCore](https://www.nuget.org/packages/NArk.Storage.EfCore)** | EF Core persistence for all Ark state |

## Quick Links

| | |
|---|---|
| **[Getting Started](docs/articles/getting-started.md)** | Install packages and set up your first Ark wallet |
| **[Architecture](docs/articles/architecture.md)** | SDK layering, DI registration, and extensibility |
| **[Wallets](docs/articles/wallets.md)** | HD and SingleKey wallet management |
| **[Spending](docs/articles/spending.md)** | Automatic and manual coin selection, sub-dust outputs |
| **[Assets](docs/articles/assets.md)** | Issuance, transfer, burn, and querying Arkade assets |
| **[Swaps](docs/articles/swaps.md)** | Lightning integration via Boltz |
| **[Storage](docs/articles/storage.md)** | EF Core setup, entity reference |
| **[API Reference](api/index.md)** | Auto-generated API documentation |

## Links

- [GitHub Repository](https://github.com/arkade-os/dotnet-sdk)
- [NuGet Packages](https://www.nuget.org/profiles/ArkLabs)
- [Ark Protocol](https://ark-protocol.org)
- [Sample Wallet App](https://github.com/arkade-os/dotnet-sdk/tree/master/samples/NArk.Wallet)

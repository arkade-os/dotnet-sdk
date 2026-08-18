---
_layout: landing
---

# NArk .NET SDK

> The official .NET SDK for [Arkade](https://arkadeos.com), an open execution engine for Bitcoin.

NArk gives .NET applications everything they need to build on Arkade: wallet management, the virtual output lifecycle, intent-based payments, asset support, Lightning swaps through Boltz, and pluggable storage. Every transaction it builds is a Bitcoin transaction.

## Packages

| Package | Description |
|---|---|
| **[NArk](https://www.nuget.org/packages/NArk)** | Meta-package — pulls in Core + Swaps |
| **[NArk.Abstractions](https://www.nuget.org/packages/NArk.Abstractions)** | Interfaces and domain types |
| **[NArk.Core](https://www.nuget.org/packages/NArk.Core)** | Wallet, virtual output, intent, batch, and asset logic |
| **[NArk.Swaps](https://www.nuget.org/packages/NArk.Swaps)** | Boltz submarine/reverse/chain swap client |
| **[NArk.Storage.EfCore](https://www.nuget.org/packages/NArk.Storage.EfCore)** | EF Core persistence for all Arkade state |

## Quick Links

| | |
|---|---|
| **[Getting Started](docs/articles/getting-started.md)** | Install packages and set up your first Arkade wallet |
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
- [Arkade](https://arkadeos.com)
- [Arkade Documentation](https://docs.arkadeos.com)
- [Live Wallet Demo](wallet/) — Blazor WASM sample app running entirely in-browser
- [Sample Wallet Source](https://github.com/arkade-os/dotnet-sdk/tree/master/samples/NArk.Wallet)

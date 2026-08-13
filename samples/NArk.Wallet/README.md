# Arkade Wallet — Sample App

A neo-bank style wallet built with the NArk .NET SDK. It showcases all SDK features: wallets, VTXOs, spending, receiving, assets, and swaps. It runs entirely in the browser via Blazor WASM.

## Architecture

```
┌─────────────────────────────────┐
│   Blazor WASM (PWA)             │  ← Browser
│   NArk.Wallet.Client            │
│   ┌───────────┐ ┌─────────────┐ │
│   │ NArk SDK  │ │ SQLite via  │ │
│   │ (Core +   │ │ OPFS        │ │
│   │  Swaps)   │ │ (SqliteWasm │ │
│   └─────┬─────┘ │  Blazor)    │ │
│         │ REST   └─────────────┘ │
└─────────┼───────────────────────┘
          ▼
    ┌──────────┐
    │  arkd    │
    └──────────┘
```

The full NArk SDK runs in-browser via WebAssembly. `RestClientTransport` talks directly to arkd's REST API. Storage is persisted in the browser via SQLite over OPFS (Origin Private File System) using [SqliteWasmBlazor](https://github.com/b-straub/SqliteWasmBlazor).

The Gateway is a minimal static file server that serves the Blazor WASM app and sets required COOP/COEP headers for `SharedArrayBuffer` support.

## Prerequisites

- .NET 10 SDK (preview)
- An arkd server (defaults to Mutinynet at `https://mutinynet.arkade.sh`)

## Quick Start

```bash
cd samples/NArk.Wallet/NArk.Wallet.Gateway
dotnet run
```

Open `https://localhost:5001` in your browser.

## Features Demonstrated

| Feature | SDK Interface | Client Service Method |
|---------|--------------|----------------------|
| Create wallet (HD) | `WalletFactory`, `IWalletStorage` | `ArkWalletService.CreateWallet()` |
| Restore wallet | `HdWalletRecoveryService.ScanAsync` | `ArkWalletService.RestoreWallet()` |
| Get balance | `ISpendingService.GetAvailableCoins` | `ArkWalletService.GetBalance()` |
| List VTXOs | `IVtxoStorage.GetVtxos` | `ArkWalletService.GetVtxos()` |
| Send payment | `ISpendingService.Spend` | `ArkWalletService.Send()` |
| Receive addresses | `IArkadeAddressProvider.GetNextContract` | `ArkWalletService.GetReceiveInfo()` |
| List swaps | `ISwapStorage.GetSwaps` | `ArkWalletService.GetSwaps()` |
| Issue asset | `IAssetManager.IssueAsync` | `ArkWalletService.IssueAsset()` |
| Burn asset | `IAssetManager.BurnAsync` | `ArkWalletService.BurnAsset()` |

## Wallets

**Create Wallet** generates a fresh 12-word BIP39 recovery phrase and creates an HD wallet
(`WalletType.HD`, descriptor `tr([fp/86'/{coin}'/0']xpub/0/*)`). The phrase is shown once on a
backup screen before the wallet becomes active, and can be re-read later from Settings → Backup
Secret. Because the wallet is hierarchical-deterministic, every receive derives a new contract,
and the phrase alone is enough to rebuild state on another device.

**Restore Wallet** accepts either a BIP39 phrase or a legacy `nsec1…` single key. The secret is
validated client-side before import (`ArkWalletService.ValidateSecret`). For HD wallets, restore
then runs `HdWalletRecoveryService.ScanAsync`, a gap-limit sweep across every registered
`IContractDiscoveryProvider` (arkd indexer, on-chain boarding, Boltz swaps), so prior contracts,
VTXOs and swaps reappear in local storage. `nsec` wallets have no derivation index and skip the
scan.

## Configuration

To switch networks, modify the `ArkNetworkConfig` in `Program.cs`:
- `ArkNetworkConfig.Mainnet` — Production
- `ArkNetworkConfig.Mutinynet` — Signet (default)
- `ArkNetworkConfig.Regtest` — Local development

## Project Structure

```
samples/NArk.Wallet/
├── NArk.Wallet.Gateway/    # Static file server (COOP/COEP headers)
│   └── Program.cs           # Minimal host
└── NArk.Wallet.Client/     # Blazor WASM PWA (full SDK in-browser)
    ├── Pages/               # Route pages (Home, Send, Receive, Swap, Assets)
    ├── Layout/              # App shell with bottom navigation
    ├── Services/            # ArkWalletService, WalletDbContext, WasmSafetyService
    │   ├── ArkWalletService.cs       # Wraps SDK services (replaces REST API client)
    │   ├── ArkServiceStartup.cs      # Manual IHostedService startup for WASM
    │   ├── WalletDbContext.cs         # EF Core context with SqliteWasmBlazor
    │   ├── WasmSafetyService.cs       # In-browser ISafetyService
    │   └── FallbackChainTimeProvider.cs
    └── wwwroot/             # Static assets, CSS, PWA manifest
```

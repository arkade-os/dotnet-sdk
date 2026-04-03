# Architecture

## Package Layering

```
NArk (meta-package)
 ├── NArk.Core
 │    ├── Services (spending, batches, VTXO sync, sweeping, intents)
 │    ├── Wallet (WalletFactory, signers, address providers)
 │    ├── Hosting (DI extensions, ArkApplicationBuilder)
 │    └── Transport (gRPC client for Ark server communication)
 │
 ├── NArk.Swaps
 │    ├── Boltz client (submarine & chain swaps)
 │    └── Swap management service
 │
 └── NArk.Abstractions
      ├── Domain types (ArkCoin, ArkVtxo, ArkContract, ArkAddress, etc.)
      ├── Storage interfaces (IVtxoStorage, IContractStorage, IIntentStorage)
      └── Wallet interfaces (IWalletProvider, IArkadeWalletSigner)

NArk.Storage.EfCore (optional, provider-agnostic persistence)
```

## Dependency Direction

- **NArk.Abstractions** has no SDK dependencies (only NBitcoin)
- **NArk.Core** depends on Abstractions
- **NArk.Swaps** depends on Core
- **NArk.Storage.EfCore** depends on Core + Swaps (implements all storage interfaces)

## Extensibility Points

The SDK is built around pluggable interfaces. Provide your own implementations or use the defaults:

| Interface | Purpose | Default |
|---|---|---|
| `IVtxoStorage` | VTXO persistence | `EfCoreVtxoStorage` |
| `IContractStorage` | Contract persistence | `EfCoreContractStorage` |
| `IIntentStorage` | Intent persistence | `EfCoreIntentStorage` |
| `ISwapStorage` | Swap persistence | `EfCoreSwapStorage` |
| `IWalletProvider` | Wallet creation/retrieval | `DefaultWalletProvider` |
| `IArkadeWalletSigner` | Transaction signing | HD/SingleKey signers |
| `ICoinSelector` | UTXO selection strategy | `DefaultCoinSelector` |
| `IFeeEstimator` | Fee estimation | `DefaultFeeEstimator` |
| `ISafetyService` | Transaction safety checks | User-provided |
| `IChainTimeProvider` | Blockchain time | User-provided |

## Transport Layer

Communication with the Ark server (arkd) uses gRPC:

- **`GrpcClientTransport`** — direct gRPC connection
- **`RestClientTransport`** — REST/JSON fallback (e.g., for browser environments)
- **`CachingClientTransport`** — caches server info to reduce round-trips

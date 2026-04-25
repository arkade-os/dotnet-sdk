# Architecture

## Package Layering

```
NArk (meta-package)
 ├── NArk.Core
 │    ├── Services (spending, batches, VTXO sync, sweeping, intents)
 │    ├── Wallet (WalletFactory, signers, address providers)
 │    ├── Hosting (DI extensions, ArkApplicationBuilder)
 │    └── Transport (gRPC client for Arkade server communication)
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

Communication with the Arkade server (arkd) uses gRPC:

- **`GrpcClientTransport`** — direct gRPC connection
- **`RestClientTransport`** — REST/JSON fallback (e.g., for browser environments)
- **`CachingClientTransport`** — caches server info to reduce round-trips

## Opt-In Feature Wiring

A few subsystems are intentionally not registered by `AddArkCoreServices` because they need consumer-supplied configuration:

- **Delegation** — `AddArkDelegation(delegatorUri)` registers `IDelegatorProvider`, `DelegationService`, `DelegateContractTransformer`, and `DelegationMonitorService` (hosted). Wraps `IWalletProvider` to produce `ArkDelegateContract`s for HD wallets.
- **Payment tracking** — `AddArkPaymentTracking()` registers `IPaymentStorage`, `IPaymentRequestStorage`, and `PaymentTrackingService` (hosted). Requires `modelBuilder.ConfigureArkPaymentEntities()` in your `DbContext`.
- **Swaps** — `AddArkSwapServices()` registers `SwapsManagementService` and the Boltz client. Configured via `ArkNetworkConfig.BoltzUri`.

Skipping any of these keeps the dependency graph and schema minimal for plugins that don't need them.

## Vendored NBitcoin.Scripting

`NArk.Abstractions/Scripting/` contains a pruned copy of the `NBitcoin.Scripting` namespace from the NBitcoin 9.x era (`OutputDescriptor`, `PubKeyProvider`, parser combinators). NBitcoin 10 removed this subsystem in favor of BIP388 `WalletPolicy` / `Miniscript`; NArk continues to use the classic `OutputDescriptor` type because its semantics (HD derivation, origin info, non-Taproot wrapping) match arkd's wire format and preserve 33-byte compressed keys with parity. Only the descriptor parsing and HD-derivation parts are vendored — the script-tree inference and signing-repo interactions that depended on NBitcoin internals were stripped.

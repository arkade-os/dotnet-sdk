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
| `IBitcoinBlockchain` | Chain time, median time past, UTXO lookup, broadcast, fee estimation | `NBXplorerBlockchain` / `EsploraBlockchain` / `RpcBlockchain` |

### Chain time and relative timelocks

`IBitcoinBlockchain` exposes two clocks, and the distinction matters wherever a BIP-68 relative timelock is evaluated (chiefly the unilateral-exit CSV delay):

- `GetChainTime` returns the tip's height plus its **median time past** (BIP 113). `TimeHeight.Timestamp` must be MTP, not the tip block's `nTime` — MTP is what consensus compares timelocks against.
- `GetMedianTimePastAsync(blockHeight)` returns a historical block's MTP. It carries a default implementation that throws `NotSupportedException`, so a backend that can't answer fails loudly rather than reporting a timelock as perpetually immature.

The delays arkd advertises (`unilateral_exit_delay`, `boarding_exit_delay`) decode into `NBitcoin.Sequence` values that may be **block-based** or **time-based** — arkd overloads one integer, treating values ≥ 512 as seconds. Their raw `Sequence.Value` is not a block count in the time-based case (bit 22 is set and the delay is stored in 512-second units), so consumers branch on `Sequence.LockType`. `NArk.Core.Exit.CsvMaturity` encapsulates that check; see the Unilateral Exit section of the README.

## Transport Layer

Communication with the Arkade server (arkd) uses gRPC:

- **`GrpcClientTransport`** — direct gRPC connection
- **`RestClientTransport`** — REST/JSON fallback (e.g., for browser environments)
- **`CachingClientTransport`** — caches server info to reduce round-trips

### VTXO script subscription (in-place updates)

arkd's indexer subscription is a long-lived stream whose watched script set can be mutated while it stays open. The transport exposes this as three composable primitives:

- `SubscribeForScriptsAsync(scripts, subscriptionId)` — create a subscription (`subscriptionId == null`) or **add** scripts to an existing one.
- `UnsubscribeForScriptsAsync(subscriptionId, scripts)` — **remove** scripts (or all, tearing the subscription down, when `scripts == null`).
- `GetVtxoSubscriptionStreamAsync(subscriptionId)` — open the server stream; scripts added/removed via the calls above are routed onto this already-open stream.

`VtxoSynchronizationService` uses these to keep one stream open and update the watched set **in place** when contracts come and go, rather than tearing the stream down and resubscribing on every change. It recreates the subscription if arkd reports it gone (TTL after a disconnect), and tears it down when the active set is empty. The 5-second fresh-derive safety-net poll remains the backstop, so detection never depends on the stream surviving. (`GetVtxoToPollAsStream` is kept as a one-shot convenience built on these primitives.)

## Opt-In Feature Wiring

A few subsystems are intentionally not registered by `AddArkCoreServices` because they need consumer-supplied configuration:

- **Delegation** — `AddArkDelegation(delegatorUri)` registers `IDelegatorProvider`, `DelegationService`, `DelegateContractTransformer`, and `DelegationMonitorService` (hosted). Wraps `IWalletProvider` to produce `ArkDelegateContract`s for HD wallets.
- **Payment tracking** — `AddArkPaymentTracking()` registers `IPaymentStorage`, `IPaymentRequestStorage`, and `PaymentTrackingService` (hosted). Requires `modelBuilder.ConfigureArkPaymentEntities()` in your `DbContext`.
- **Swaps** — `AddArkSwapServices()` registers `SwapsManagementService` and the Boltz client. Configured via `ArkNetworkConfig.BoltzUri`.

Skipping any of these keeps the dependency graph and schema minimal for plugins that don't need them.

## Vendored NBitcoin.Scripting

`NArk.Abstractions/Scripting/` contains a pruned copy of the `NBitcoin.Scripting` namespace from the NBitcoin 9.x era (`OutputDescriptor`, `PubKeyProvider`, parser combinators). NBitcoin 10 removed this subsystem in favor of BIP388 `WalletPolicy` / `Miniscript`; NArk continues to use the classic `OutputDescriptor` type because its semantics (HD derivation, origin info, non-Taproot wrapping) match arkd's wire format and preserve 33-byte compressed keys with parity. Only the descriptor parsing and HD-derivation parts are vendored — the script-tree inference and signing-repo interactions that depended on NBitcoin internals were stripped.

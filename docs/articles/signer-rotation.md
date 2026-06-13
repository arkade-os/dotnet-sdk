# Arkade Signer-Key Rotation

When an Arkade server operator rotates its signing key, existing VTXOs bound to the old signer must be re-enrolled under the current signer before the operator stops co-signing on behalf of the old key. The SDK handles this automatically for both SingleKey and HD wallets.

## How Rotation Works

When arkd rotates its signer, the old key moves into `ArkServerInfo.DeprecatedSigners` with a cutoff timestamp. Depending on where a VTXO stands relative to that cutoff, the SDK applies one of three strategies:

| Regime | Condition | Action |
|--------|-----------|--------|
| 1 — Collaborative sweep | Before cutoff (or no cutoff) | `ServerKeyRotationSweepPolicy` sweeps the VTXO into a new one under the current signer |
| 2 — Wait | After cutoff, before VTXO expiry | Operator refuses to co-sign; the coin is excluded from offchain spends and waits until it becomes recoverable |
| 3 — Recovery re-enroll | After VTXO expiry (`IsRecoverable`) | Wallet re-enrolls via the intent scheduler; the batch session skips forfeit so the old key is not needed |

Coin gathering is keyed on VTXO **script**, not contract `Active` state, so deactivating a stale default contract never strands its funds.

Regime 2 is enforced in the spendability model: `ArkCoin.CanSpendOffchain(current, deprecatedSigners)` (built on `ArkCoin.IsDeprecatedSignerPastCutoff`) returns `false` for a coin whose contract server key is a deprecated signer past its cutoff, so `SpendingService.GetAvailableCoins` keeps it out of offchain-spend selection — it can no longer be collaboratively spent. The coin stays **not** `IsRecoverable` (regime 3 still fires only on expiry), so it correctly waits rather than being re-enrolled prematurely.

## Detection

The SDK detects a rotation through two paths:

- **Mid-request** (`DIGEST_MISMATCH`): if arkd rejects a request because the cached server-info digest no longer matches, `CachingClientTransport` clears the cache and raises `IServerInfoCacheInvalidation.ServerInfoChanged` before re-throwing `DigestMismatchException`.
- **TTL refresh**: when the 5-minute server-info cache expires and the fresh fetch returns a different digest, `ServerInfoChanged` is raised with `Reason = TtlExpiry`.

Both paths funnel through the same event so consumers need only one subscription.

## SingleKey Wallet Reconciliation

A SingleKey wallet's "Default" receive contract is derived from `ArkServerInfo.SignerKey`. After rotation the old-signer default becomes stale. `ContractReconciliationService` keeps defaults aligned automatically:

- On **startup** — reconciles all SingleKey wallets (catches rotations that happened while the app was offline).
- On **`WalletSaved`** — reconciles the newly created or updated wallet.
- On **`ServerInfoChanged`** — reconciles all SingleKey wallets.

Reconciliation calls `ISingleKeyDefaultEnsurer.EnsureDefaultAsync` to upsert the current-signer default, then deactivates any `Source="Default"` row whose script no longer matches. This is wired up automatically by `AddArkCoreServices`; no extra registration is needed.

## SingleKey Discovery After Rotation

`SingleKeyVtxoRecoveryService.DiscoverAsync` probes every registered `IContractDiscoveryProvider` with the wallet's flat `tr(pubkey)` descriptor. `IndexerVtxoDiscoveryProvider` internally cross-products `{current signer ∪ deprecated signers}`, so VTXOs stranded under a rotated signer are re-discovered automatically and persisted as `Active` contracts.

## Version and Digest Headers

Every outgoing gRPC and REST request carries two headers:

| Header | Value | Purpose |
|--------|-------|---------|
| `X-Build-Version` | `ArkdVersion.TargetBuild` (e.g. `0.9.7`) | Lets arkd reject SDKs that are too old (`BUILD_VERSION_TOO_OLD`) |
| `X-Digest` | Current server-info digest | Lets arkd detect stale cached configuration (`DIGEST_MISMATCH`) |

The headers are injected by `BuildVersionInterceptor` (gRPC) and `BuildVersionHandler` (REST). Both throw typed exceptions on rejection:

- `IncompatibleSdkVersionException` — SDK build version too old; upgrade the NArk SDK package.
- `DigestMismatchException` — Server configuration changed mid-session; call `GetServerInfoAsync` to refresh then retry.

> **Note:** The `RestClientTransport(HttpClient)` constructor overload (for Blazor WASM / `IHttpClientFactory`) does not insert `BuildVersionHandler` into the pipeline. Use the `RestClientTransport(string uri)` URI-based constructor if automatic digest/version checking is required.

## Listening for Rotation Events

Inject `IServerInfoCacheInvalidation` to react to a rotation in your own services:

```csharp
public class MyRotationAwareService(IServerInfoCacheInvalidation serverInfoCache)
{
    public void Start()
    {
        serverInfoCache.ServerInfoChanged += OnServerInfoChanged;
    }

    private void OnServerInfoChanged(object? sender, ServerInfoChangedEventArgs e)
    {
        // e.Reason: ManualInvalidation | DigestMismatch | TtlExpiry
        // e.PreviousDigest / e.NewDigest available when Reason == TtlExpiry
        Console.WriteLine($"Arkade server info changed: {e.Reason}");
    }
}
```

`IServerInfoCacheInvalidation` is DI-aliased to the same `CachingClientTransport` singleton that `IClientTransport` resolves to, so no extra registration is needed — just inject the interface.

# HD Wallet Recovery

When a user re-imports an HD wallet from its mnemonic, the SDK has no local record of the contracts that were derived under the previous instance — the script-pubkeys, swap VHTLCs, boarding addresses, etc. The **HD recovery scanner** rebuilds that state by sweeping derivation indices and asking external sources whether each one was ever used.

## How it works

1. The wallet is identified as HD (single-key wallets are rejected; they have no notion of indexing).
2. Starting from `StartIndex` (default 0), the scanner derives the concrete `OutputDescriptor` at each index from the wallet's `AccountDescriptor`.
3. Every registered `IContractDiscoveryProvider` is asked whether *its* source of truth has any record of that descriptor's pubkey. The default providers ship with the SDK:

   | Provider | Source | Detects |
   |---|---|---|
   | `IndexerVtxoDiscoveryProvider` | arkd indexer | Any VTXO ever recorded against the index's `ArkPaymentContract` |
   | `BoardingUtxoDiscoveryProvider` | NBXplorer / Esplora (whichever `IBoardingUtxoProvider` is registered) | Any historical UTXO at the index's `ArkBoardingContract` on-chain address |
   | `BoltzSwapDiscoveryProvider` | Boltz `/v2/swap/restore` | Any swap (submarine or reverse) involving the index's user pubkey |

4. If **any** provider reports usage, the index counts as used: the gap counter resets, the scanner records every contract the providers reconstructed, and continues to the next index.
5. The scan stops once `GapLimit` consecutive unused indices are seen, or once `MaxIndex` is reached.
6. Discovered contracts are persisted via `IContractStorage` (deduped by script in case multiple providers reconstructed the same one) and `wallet.LastUsedIndex` is bumped to `HighestUsedIndex + 1` so subsequent derivations don't collide with recovered scripts.

## Setup

`AddArkCoreServices` registers `HdWalletRecoveryService`, the indexer provider, and a conditional boarding provider (active only when an `IBoardingUtxoProvider` is also registered). `AddArkSwapServices` adds the Boltz provider. So the recovery surface is ready as long as the application opted into core + swaps:

```csharp
services.AddArkCoreServices();
services.AddArkSwapServices();
services.AddSingleton<IBoardingUtxoProvider, NBXplorerBoardingUtxoProvider>(); // or Esplora
```

## Usage

```csharp
var recovery = serviceProvider.GetRequiredService<HdWalletRecoveryService>();

// Default scan: gap=20, max=10000
var report = await recovery.ScanAsync(walletId);

Console.WriteLine($"Highest used index: {report.HighestUsedIndex}");
Console.WriteLine($"Scanned: {report.ScannedCount}");
foreach (var (provider, hits) in report.ProviderHits)
    Console.WriteLine($"  {provider}: {hits} hit(s)");

// Tune the scan for wallets known to have generated many addresses ahead of use:
var deepReport = await recovery.ScanAsync(walletId, new RecoveryOptions(GapLimit: 50));

// Resume a partial scan from a known index:
var resumed = await recovery.ScanAsync(walletId, new RecoveryOptions(StartIndex: 200, GapLimit: 20));
```

## Adding custom providers

To probe an additional source (a custom indexer, a different swap counterparty, an external escrow service, etc.), implement `IContractDiscoveryProvider` and register it in DI. The orchestrator picks all registered providers up automatically:

```csharp
public class MyCustomDiscoveryProvider : IContractDiscoveryProvider
{
    public string Name => "my-custom";

    public async Task<DiscoveryResult> DiscoverAsync(
        ArkWalletInfo wallet,
        OutputDescriptor userDescriptor,
        int index,
        CancellationToken ct)
    {
        // Derive whatever script/pubkey shape your source expects, then probe.
        var pubKeyHex = OutputDescriptorHelpers.Extract(userDescriptor).PubKey!.ToHex();
        var hits = await _myService.LookupAsync(pubKeyHex, ct);
        if (hits.Count == 0) return DiscoveryResult.NotFound;

        // If you can reconstruct an ArkContract from the hits, return it so the
        // orchestrator persists it with recovery-source metadata.
        return new DiscoveryResult(true, [/* reconstructed contracts */]);
    }
}

services.AddSingleton<IContractDiscoveryProvider, MyCustomDiscoveryProvider>();
```

Providers are queried sequentially per index; aggregate results use OR semantics — any hit counts. They MUST NOT mutate `IWalletStorage` or `IContractStorage` directly: return contracts via `DiscoveryResult.Contracts` and the orchestrator will persist them with `Source=recovery:<provider-name>` metadata. The exception is `BoltzSwapDiscoveryProvider`, which delegates to the existing `SwapsManagementService.RestoreSwaps` — that path imports VHTLC contracts itself with the canonical `Source=swap:<id>` metadata and the swap record, so the provider returns an empty contract list to avoid double-imports.

## Tuning notes

- **Gap limit**: BIP44 recommends 20. Increase if you know the wallet pre-generated many addresses without using them; decrease for a faster scan when you're confident usage is dense.
- **MaxIndex**: hard upper bound. The default of 10,000 is generous; a paranoid scan can go to `int.MaxValue`, but every index is at minimum one indexer round-trip.
- **Performance**: indexer + boarding probes are local-network-cheap (gRPC/HTTP one round-trip each). Boltz probes are one HTTP call to `/v2/swap/restore` per index — for a typical gap-of-20 scan that's ~25 calls, taking a few seconds in total.
- **Idempotency**: scans are safe to re-run. The orchestrator dedupes by script and never lowers `wallet.LastUsedIndex`.

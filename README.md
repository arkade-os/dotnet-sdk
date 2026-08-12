# NArk .NET SDK

A .NET SDK for building applications on [Arkade](https://arkadeos.com), an open execution engine for Bitcoin. Arkade makes transactions instant, low-cost, and programmable through virtual outputs, and every transaction it builds is a Bitcoin transaction.

[![NuGet](https://img.shields.io/nuget/v/NArk.svg)](https://www.nuget.org/packages/NArk)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![API Reference](https://img.shields.io/badge/API-reference-blue.svg)](https://arkade-os.github.io/dotnet-sdk/)

The generated API reference is published at [arkade-os.github.io/dotnet-sdk](https://arkade-os.github.io/dotnet-sdk/).

## Packages

| Package | Description |
|---------|-------------|
| **NArk.Abstractions** | Interfaces and domain types (`IVtxoStorage`, `IContractStorage`, `IWalletProvider`, `ArkCoin`, `ArkVtxo`, etc.) |
| **NArk.Core** | Core services: spending, batch management, VTXO sync, sweeping, wallet infrastructure, gRPC transport |
| **NArk.Swaps** | Multi-provider swap framework with pluggable providers ([Boltz](https://boltz.exchange) shipped; route-based architecture for adding others) |
| **NArk.Storage.EfCore** | Entity Framework Core storage implementations (provider-agnostic — works with PostgreSQL, SQLite, etc.) |
| **NArk** | Meta-package that pulls in `NArk.Core` + `NArk.Swaps` |

## Quick Start

### Install

```bash
dotnet add package NArk                    # Core + Swaps
dotnet add package NArk.Storage.EfCore     # EF Core persistence
```

### Minimal Setup with Generic Host

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
    .WithBlockchain<NBXplorerBlockchain>()
    .OnMainnet()
    .EnableSwaps();

// Register your DbContext and EF Core storage
builder.ConfigureServices((_, services) =>
{
    services.AddDbContextFactory<YourDbContext>(opts =>
        opts.UseNpgsql(connectionString));

    services.AddArkEfCoreStorage<YourDbContext>();
});

var app = builder.Build();
await app.RunAsync();
```

### Setup with IServiceCollection (plugin/non-host scenarios)

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

// Register remaining required services
services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
services.AddSingleton<ISafetyService, YourSafetyService>();

// Pick the blockchain backend you have a client for. Each helper registers
// a single IBitcoinBlockchain that handles chain time, UTXO lookup at a
// boarding address, broadcast, package broadcast, tx status and fee
// estimation. Last registration wins, so you can swap in a custom impl
// after the helper if you want to override one method.
services.AddNBXplorerBlockchain(network, new Uri("http://localhost:32838"));
// or: services.AddEsploraBlockchain(new Uri("https://mempool.space/api/"));
// or: services.AddRpcBlockchain(rpcClient);  // UTXO lookup not supported
```

## Architecture

```
NArk (meta-package)
 ├── NArk.Core
 │    ├── Services (spending, batches, VTXO sync, sweeping, intents)
 │    ├── Wallet (WalletFactory, signers, address providers)
 │    ├── Hosting (DI extensions, ArkApplicationBuilder)
 │    └── Transport (gRPC client for Arkade server communication)
 │
 ├── NArk.Swaps
 │    ├── Abstractions (ISwapProvider, SwapRoute, SwapAsset)
 │    ├── Boltz provider (submarine, reverse & chain swaps)
 │    └── SwapsManagementService (multi-provider router)
 │
 └── NArk.Abstractions
      ├── Domain types (ArkCoin, ArkVtxo, ArkContract, ArkAddress, etc.)
      ├── Storage interfaces (IVtxoStorage, IContractStorage, IIntentStorage)
      └── Wallet interfaces (IWalletProvider, IArkadeWalletSigner)

NArk.Storage.EfCore (optional, provider-agnostic persistence)
 ├── EF Core entity mappings
 ├── Storage implementations
 └── DI extension: AddArkEfCoreStorage<TDbContext>()
```

## Wallet Management

Wallets are described along two orthogonal axes; capability is answered by the provider, not by a tag on the data.

**Key derivation** (`WalletType`):

| `WalletType` | Script shape | Use case |
| --- | --- | --- |
| `HD` | `tr([fp/path]xpub/0/*)` | Per-contract derivation, boarding support |
| `SingleKey` | `tr(pubkey)` | Static key, simple integrations |

**Signing capability** — decided at `IWalletProvider.GetSignerAsync` time and built by composing one or more `IDescriptorSigningSource`s behind a `CompositeArkadeWalletSigner`:

| `ArkWalletInfo.Secret` | `IRemoteSignerTransport.KnowsWalletAsync` | Returns | Meaning |
| --- | --- | --- | --- |
| non-empty | — | composite with the matching local signing source | sign locally |
| null/empty | `true` | composite with `RemoteTransportSigningSource` only | sign via transport |
| null/empty | `false` (or no transport) | `null` | watch-only |

Three signing sources ship in the box — `Bip39SigningSource` (matches descriptors by master fingerprint), `NsecSigningSource` (matches by x-only pubkey), `RemoteTransportSigningSource` (delegates to an `IRemoteSignerTransport`). Implement `IDescriptorSigningSource` to plug in anything else (HWI, threshold key share, in-browser session signer, …).

Any combination of the two axes is valid — a watch-only `HD`, a remote-signed `SingleKey`, etc.

**HD Wallets** — BIP-39 mnemonic with BIP-86 taproot derivation (`m/86'/cointype'/0'`):

```csharp
var serverInfo = await transport.GetServerInfoAsync();
var wallet = await WalletFactory.CreateWallet(
    "abandon abandon abandon ... about",  // BIP-39 mnemonic
    destination: null,
    serverInfo);
// wallet.WalletType == WalletType.HD
```

**Single-Key Wallets** — nostr `nsec` format (Bech32-encoded secp256k1 key):

```csharp
var wallet = await WalletFactory.CreateWallet(
    "nsec1...",
    destination: null,
    serverInfo);
// wallet.WalletType == WalletType.SingleKey
```

**Watch-Only and Remote-Signed Wallets** — same data shape (`Secret = null` on a normal `ArkWalletInfo`); the runtime distinction is made by whether an `IRemoteSignerTransport` is registered and claims the wallet:

```csharp
// Build the wallet record once. WalletType is inferred from the descriptor shape
// (wildcard → HD, bare → SingleKey).
var wallet = await WalletFactory.CreateWatchOnlyWallet(
    accountDescriptor: "tr([abcd1234/86'/1'/0']tpub.../0/*)",
    destination: null,
    serverInfo);
// wallet.WalletType == WalletType.HD, wallet.Secret == null
await walletStorage.SaveWallet(wallet);

// To make the same wallet remote-signed instead of watch-only, register an
// IRemoteSignerTransport whose KnowsWalletAsync returns true for wallet.Id:
public class MyRemoteSignerTransport : IRemoteSignerTransport
{
    public Task<bool> KnowsWalletAsync(string walletId, CancellationToken ct) => _bridge.IsPairedAsync(walletId, ct);
    // … sign methods …
}
services.AddSingleton<IRemoteSignerTransport, MyRemoteSignerTransport>();
// GetSignerAsync now returns a CompositeArkadeWalletSigner wrapping a
// RemoteTransportSigningSource for that walletId, instead of null. Same data;
// different signer-source.
```

Save and load wallets through `IWalletStorage`:

```csharp
await walletStorage.SaveWallet(wallet);
var loaded = await walletStorage.LoadWallet(wallet.Id);
var all = await walletStorage.LoadAllWallets();
```

## Spending

Use `ISpendingService` to send Arkade transactions:

```csharp
// Automatic coin selection
var txId = await spendingService.Spend(
    walletId,
    outputs: [new ArkTxOut(recipientAddress, Money.Satoshis(10_000))]);

// Manual coin selection
var coins = await spendingService.GetAvailableCoins(walletId);
var txId = await spendingService.Spend(
    walletId,
    inputs: coins.Take(2).ToArray(),
    outputs: [new ArkTxOut(recipientAddress, Money.Satoshis(5_000))]);
```

## ArkadeCash

`ArkadeCash` is a bearer instrument: a fresh private key plus the contract parameters
needed to rebuild the Arkade payment contract it funds, packed into one bech32m string
(`arkadecash1...` on mainnet, `tarkadecash1...` on testnet/regtest). Whoever holds the
string controls the funds, so value can be handed over without the recipient sharing an
Arkade address first. The encoding matches the ArkadeCash format of the TypeScript SDK,
so a note created by either SDK can be claimed by the other.

The payload is 69 bytes: version (1) + private key (32) + Arkade server public key (32)
+ BIP68 CSV sequence (4, big-endian).

```csharp
using NArk.Abstractions;
using NArk.Core.Extensions;

var serverInfo = await transport.GetServerInfoAsync();

// Create a note with a fresh random key
var cash = ArkadeCash.Generate(
    serverInfo.SignerKey.ToXOnlyPubKey(),
    serverInfo.UnilateralExit,
    "tarkadecash");

// Fund it: send to the address of the contract the note controls
var address = cash.GetAddress(serverInfo.Network);
await spendingService.Spend(
    walletId,
    [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(10_000), address)]);

// Hand this string to the recipient — it carries the private key, treat it as a secret
string note = cash.ToString();
```

Claiming: hand the note and a destination address to `ArkadeCashService`.

```csharp
var cashService = sp.GetRequiredService<ArkadeCashService>();

if (!ArkadeCash.TryParse(note, out var claimed) || claimed is null)
    throw new FormatException("Not a valid ArkadeCash note");

using (claimed)
{
    var destination = (await contractService.DeriveContract(walletId, NextContractPurpose.Receive))
        .GetArkAddress();

    var result = await cashService.ClaimAsync(claimed, destination);
    Console.WriteLine($"Swept {result.Swept} sat");

    foreach (var left in result.Unclaimed)
        Console.WriteLine($"Left behind {left.Amount} sat at {left.Outpoint}: {left.Reason}");
}
```

The claim is thin on purpose: **nothing is persisted**. No contract is imported and the note's key
never reaches wallet storage — it signs one offchain transaction per VTXO, in memory, straight to
the destination. That is what makes a note claimable at all: importing its contract would only
register a script to watch, since the wallet holds no key matching the note's descriptor and could
never sign for it.

One transaction per VTXO means a single stale or rejected input dents only its own sweep instead of
sinking the claim. Anything that could not be swept comes back in `result.Unclaimed` with a reason
(`AlreadySpent`, `ServerSwept`, `Subdust`, `AssetBearing`, `SweepFailed`) rather than as an
exception, so claiming an already-claimed note is a report, not a failure.

Because nothing is imported, the claim also does not care whether the Arkade server has rotated its
signer since the note was funded: the note is spent under the key it was issued against, which the
operator keeps co-signing until that key's deprecation cutoff passes.

`ArkadeCash` owns its private key and implements `IDisposable` — dispose it once the note
has been claimed or persisted.

## Wallet Recovery

Rebuild a wallet's local state — contracts, the HD derivation index, funds (VTXOs)
and boltz swap data — from on-chain / indexer / boltz sources, after importing a
wallet into empty storage. Use the unified, wallet-type-agnostic
`IWalletRecoveryService` (registered by `AddArkSwapServices`):

```csharp
var recovery = sp.GetRequiredService<IWalletRecoveryService>();
var report = await recovery.RecoverAsync(walletId);
// report.HdScan, report.ContractsRecovered, report.RestoredSwaps,
// report.SwapAudit, report.FinalizedPendingTxIds, report.FundsScriptsSynced
```

It dispatches by wallet type: **HD** wallets get a gap-limit index scan that
discovers contracts across the **current and every deprecated server signer** — so
funds locked under a rotated/legacy server key are still found — and restores boltz
swaps in-line; **SingleKey** wallets re-derive their one deterministic contract and
restore swaps directly. Both then finalize any in-flight Arkade transactions and
resync funds.

Discovery is pluggable via `IContractDiscoveryProvider` (indexer / boarding / boltz).
To also probe delegate (auto-renewal) scripts during recovery, register a
`RecoveryDelegateConfig` with the delegate key descriptors.

## Assets

The SDK supports issuing, transferring, and burning assets on Arkade. Assets are encoded as `AssetGroup` entries inside an OP_RETURN output (an "asset packet") attached to each Arkade transaction. The asset ID is derived from `{txid, groupIndex}` after submission.

Asset packets are **deterministic**: `AssetPacketBuilder` emits groups in a stable order (by asset id, then group index) regardless of input order, so the same logical transaction always serializes to identical bytes. This matches the ordering used by the other Arkade SDKs (ts-sdk / rust-sdk) and makes packets reproducible and cross-SDK fixture-comparable.

### Issuance

Use `IAssetManager` to create new assets:

```csharp
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1000));

// result.AssetId  — the unique asset identifier
// result.ArkTxId  — the Arkade transaction that created it
```

Issue with metadata:

```csharp
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(
        Amount: 1000,
        Metadata: new Dictionary<string, string>
        {
            { "name", "My Token" },
            { "ticker", "MTK" },
            { "decimals", "8" }
        }));
```

### Controlled Issuance & Reissuance

A control asset acts as a minting key — only the holder can issue more supply:

```csharp
// Issue a control asset (amount=1, acts as the minting authority)
var control = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1));

// Issue a token controlled by that asset
var token = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1000, ControlAssetId: control.AssetId));

// Reissue more supply later (requires holding the control asset)
await assetManager.ReissueAsync(walletId,
    new ReissuanceParams(control.AssetId, Amount: 500));
```

### Transfer

Asset transfers use the standard `SpendingService.Spend()` with `ArkTxOut.Assets`:

```csharp
await spendingService.Spend(walletId,
[
    new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, recipientAddress)
    {
        Assets = [new ArkTxOutAsset(assetId, 400)]
    }
]);
// Automatic coin selection handles BTC fees and asset change.
// Sender retains remaining units (e.g. 600 of 1000) as asset change.
```

### Burn

Reduce the circulating supply of an asset:

```csharp
await assetManager.BurnAsync(walletId,
    new BurnParams(assetId, Amount: 400));
// Remaining 600 units are returned as change
```

### Querying Assets

Check asset balances from local VTXO storage:

```csharp
var coins = await spendingService.GetAvailableCoins(walletId);
foreach (var coin in coins.Where(c => c.Assets is { Count: > 0 }))
{
    foreach (var asset in coin.Assets!)
        Console.WriteLine($"Asset {asset.AssetId}: {asset.Amount} units");
}
```

Query asset details from the Arkade server:

```csharp
var details = await transport.GetAssetDetailsAsync(assetId);
// details.Supply — total circulating supply
// details.AssetId — the asset identifier
// details.Metadata — key-value metadata (if set during issuance)
```

## Delegation

Delegation solves the VTXO liveness problem: VTXOs expire if not renewed. A delegate service (e.g., [Fulmine](https://github.com/ArkLabsHQ/fulmine)) participates in batches on your behalf, rolling VTXOs over before expiry.

### Automated Delegation

When `AddArkDelegation` is configured, the SDK automatically:
1. **Derives delegate contracts** — HD wallets produce `ArkDelegateContract` instead of `ArkPaymentContract` for Receive/SendToSelf operations
2. **Auto-delegates VTXOs** — when VTXOs arrive at delegate contract addresses, the SDK builds partially signed intent + ACP forfeit txs and sends them to the delegator

```csharp
services.AddArkCoreServices();

// Enable automated delegation (Fulmine delegator gRPC endpoint)
services.AddArkDelegation("http://localhost:7012");

// That's it. HD wallets will now:
// - Derive ArkDelegateContract for new receive/change addresses
// - Auto-delegate incoming VTXOs to the delegator on receipt
// nsec wallets (hashlock/note contracts) are unaffected.
```

The delegate contract has three spending paths, committed to the taproot tree in this order
(`[CollaborativePath, ExitPath, DelegatePath]`) to match the canonical Arkade SDK layout:
- **CollaborativePath** (User + Server, 2-of-2) — collaborative spending, same as a regular payment contract
- **ExitPath** (User only, after CSV delay) — unilateral recovery
- **DelegatePath** (User + Delegate + Server, 3-of-3) — used by the delegator for ACP forfeit txs

The auto-delegation monitor skips any VTXO whose value cannot cover the operator's intent fee and
still leave at least the server dust threshold — the delegation would be rejected with
`AMOUNT_TOO_LOW`. Consolidate bare-dust VTXOs (typically asset VTXOs) with a larger VTXO to make them
delegable; the skip is not sticky, so the VTXO is re-evaluated on its next storage notification.

### Manual Delegation

For fine-grained control, you can manually construct delegate contracts and delegate VTXOs:

```csharp
// Get delegator info
var info = await delegationService.GetDelegatorInfoAsync();

// Create a delegate contract
var delegateContract = new ArkDelegateContract(
    serverInfo.SignerKey,
    serverInfo.UnilateralExit,
    userKey,
    KeyExtensions.ParseOutputDescriptor(info.Pubkey, network),
    cltvLocktime: new LockTime(currentHeight + 100)); // optional safety window

// Send VTXOs to the delegate contract address
await spendingService.Spend(walletId,
    outputs: [new ArkTxOut(delegateContract.GetArkAddress(), amount)]);

// Delegate to the delegator
await delegationService.DelegateAsync(
    intentMessage: intentJson,
    intentProof: proofPsbtBase64,
    forfeitTxs: forfeitTxHexArray,
    rejectReplace: false);
```

The CLTV locktime is optional — when set, it prevents the delegate from acting before a specific block height, giving the owner a safety window.

### Custom Contract Delegation

The SDK uses an `IDelegationTransformer` pattern to support delegating different contract types. The built-in `DelegateContractDelegationTransformer` handles `ArkDelegateContract` VTXOs and is registered by `AddArkDelegation`. Register additional transformers for other contract types *after* calling `AddArkDelegation`:

```csharp
services.AddArkDelegation("http://localhost:7012");
services.AddTransient<IDelegationTransformer, MyCustomDelegationTransformer>();
```

> Note: `DelegationService` and the default `IDelegationTransformer` are only registered by `AddArkDelegation`. `AddArkCoreServices` alone does not include delegation services.

Each transformer implements:
- `CanDelegate(walletId, contract, delegatePubkey)` — check eligibility
- `GetDelegationScriptBuilders(contract)` — return (intentScript, forfeitScript) for building delegation artifacts

## Collaborative Exits (On-chain)

Move funds from Arkade back to the Bitcoin base layer:

```csharp
var btcTxId = await onchainService.InitiateCollaborativeExit(
    walletId,
    new ArkTxOut(bitcoinAddress, Money.Satoshis(50_000)));
```

## Querying Intents by Proof

Retrieve registered intents by proving ownership of any input coin via a BIP-322-style proof:

```csharp
// Create a signed ownership proof for a coin
var (proof, message) = await IntentProofHelper.CreateIntentOwnershipProofAsync(
    coin, signer, network);

// Query arkd for intents registered with this coin
var intents = await transport.GetIntentsByProofAsync(proof, message);
```

The `IntentProofHelper.CreateBip322Psbt` and `IntentProofHelper.SignBip322Proof` building blocks are also available separately for delegation and other proof flows.

## Boarding (On-chain → Arkade)

Boarding lets users move onchain Bitcoin UTXOs into the Arkade VTXO tree. The user deposits BTC to a boarding address (a P2TR output with a collaborative spend path and a CSV-locked unilateral exit). Once confirmed, the intent/batch pipeline automatically picks up the boarding UTXO — no manual intervention needed.

### 1. Derive a Boarding Address

```csharp
var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
    walletId,
    NextContractPurpose.Boarding);

// Get the on-chain P2TR (bc1p...) address for the user to deposit BTC to
var onchainAddress = boardingContract.GetOnchainAddress(network);
```

### 2. Sync On-chain UTXOs

`BoardingUtxoSyncService` polls a blockchain indexer for confirmed UTXOs at your boarding addresses and upserts them into VTXO storage. It depends on `IBitcoinBlockchain` — register one of the built-in backends:

```csharp
// Option A: Esplora (mempool.space, Chopsticks, etc.)
// ArkNetworkConfig.{Mainnet,Mutinynet,Regtest} carry per-network
// endpoint defaults that mirror the canonical Arkade ts-sdk:
//
//   Network    EsploraUri                                 ElectrumWsUri                              ElectrumTcpUri
//   Mainnet    https://mempool.arkade.sh/api              wss://electrum.arkade.sh                   tcp://electrum.arkade.sh:50001
//   Mutinynet  https://mempool.mutinynet.arkade.sh/api    wss://electrum.mutinynet.arkade.sh         tcp://electrum.mutinynet.arkade.sh:50001
//   Regtest    http://localhost:3000                      ws://localhost:50003                       tcp://localhost:50000
//
// ElectrumWsUri is the websocket URL — wss://electrum.arkade.sh
// terminates at the host's port 443. ElectrumTcpUri is verified at the
// protocol layer against `server.version`: public Ark Labs Fulcrum
// instances only expose :50001 (plain Electrum binary protocol). 50002
// TCP+TLS is NOT exposed — for TLS use the WSS endpoint via
// ElectrumWsUri. (ts-sdk's source comment listing 50001/50002/50003 is
// stale.) Regtest uses nigiri's electrs on :50000 for the binary
// protocol — 30000 on the same host is electrs's HTTP REST, a
// different protocol.
services.AddEsploraBlockchain(new Uri(ArkNetworkConfig.Mainnet.EsploraUri!));
// or pass your own URL: services.AddEsploraBlockchain(new Uri("https://mempool.space/api/"));

// Option B: NBXplorer (BTCPay Server, self-hosted)
services.AddNBXplorerBlockchain(network, new Uri("http://localhost:32838"));

// Option C: Bitcoin Core RPC (does NOT support UTXO lookup — chain time
// + broadcast + fee estimation only; pair with one of the above if you
// also need boarding sync)
services.AddRpcBlockchain(rpcClient);

services.AddSingleton<BoardingUtxoSyncService>();

// Register the poll service — automatically polls every 30s
// when unspent boarding VTXOs exist
services.AddSingleton<BoardingUtxoPollService>();
services.AddHostedService(sp => sp.GetRequiredService<BoardingUtxoPollService>());
```

The `BoardingUtxoPollService` automatically checks for unspent boarding VTXOs every 30 seconds and syncs confirmation state changes. It complements event-driven sync (e.g., NBXplorer transaction events) to catch missed events during provider reconnects or block confirmations.

Once a boarding UTXO is synced and confirmed, the SDK's `IntentGenerationService` automatically creates an intent for it. The next batch moves it into the VTXO tree.

While a boarding UTXO is still in the mempool it is stored with `Metadata["Confirmed"] = "False"`. Such VTXOs report `ArkVtxo.IsUnconfirmedOnchain() == true`, are excluded from `SpendingService.GetAvailableCoins`, and cannot be settled (arkd rejects unconfirmed boarding inputs). Display them as pending rather than spendable until the funding tx confirms.

### 3. Handle Expired Boarding UTXOs (Optional)

If a boarding UTXO isn't batched before its CSV timelock expires, `OnchainSweepService` detects it. Register a custom `IOnchainSweepHandler` to control what happens:

```csharp
public class MySweepHandler : IOnchainSweepHandler
{
    public async Task<bool> HandleExpiredUtxoAsync(
        string walletId, ArkVtxo vtxo, ArkContractEntity contract,
        CancellationToken ct)
    {
        // Sweep to a new boarding address, cold storage, etc.
        return true; // true = handled, false = fall back to default
    }
}

services.AddSingleton<IOnchainSweepHandler, MySweepHandler>();
```

Then call `SweepExpiredUtxosAsync()` periodically:

```csharp
var sweepService = new OnchainSweepService(
    vtxoStorage, contractStorage, chainTimeProvider,
    contractService, walletProvider, sweepHandler);

await sweepService.SweepExpiredUtxosAsync(ct);
```

## Unilateral Exit

If the Arkade server goes offline or becomes uncooperative, users can **unilaterally exit** by broadcasting the chain of virtual transactions from commitment tx to their VTXO leaf, waiting a CSV timelock, then claiming funds onchain.

### Setup

```csharp
services.AddUnilateralExit(
    configureVirtualTx: opts =>
    {
        opts.DefaultMode = VirtualTxMode.Lite;  // Default: txids + expiry only; hex fetched on exit
        opts.MinExitWorthAmount = 1000;         // Skip tiny VTXOs not worth exiting
    },
    configureWatchtower: opts =>
    {
        opts.PollInterval = TimeSpan.FromSeconds(60);
    });

// Wire the single IBitcoinBlockchain (chain time + UTXO lookup + broadcast +
// package broadcast + tx status + fee estimation) in one call. Pick the
// backend you have a client for: AddNBXplorerBlockchain, AddEsploraBlockchain,
// or AddRpcBlockchain. RPC does not implement UTXO lookup (Bitcoin Core has
// no native address index). Last registration wins — register a custom
// impl afterwards to swap the whole backend.
services.AddNBXplorerBlockchain(explorerClient);

// Opt in to durable EF Core storage for sessions + chains (mirrors the
// payment-tracking entity opt-in). Skip if you'd rather use in-memory
// storage or the stateless one-shot API below.
modelBuilder.ConfigureArkExitEntities();

// Opt in to background pre-fetching of chain data on every VTXO arrival
// (subscribes to IVtxoStorage.VtxosChanged). Without this, chains are
// fetched lazily when StartExitAsync is invoked.
services.AddVirtualTxAutoFetch();

// Optional: run watchtower as background service
services.AddExitWatchtowerBackgroundService();
```

### Starting an Exit

```csharp
var exitService = serviceProvider.GetRequiredService<UnilateralExitService>();

// Exit specific VTXOs
var sessions = await exitService.StartExitAsync(
    walletId,
    vtxoOutpoints,
    claimAddress,     // Bitcoin address to receive claimed funds
    cancellationToken);

// Or exit all VTXOs in a wallet
var sessions = await exitService.StartExitForWalletAsync(
    walletId, claimAddress, cancellationToken);
```

### Progressing Exits

Call `ProgressExitsAsync` periodically to advance exit sessions through their state machine:

```csharp
// Broadcasting → AwaitingCsvDelay → Claimable → Claiming → Completed
await exitService.ProgressExitsAsync(cancellationToken);
```

The exit watchtower background service does this automatically if registered.

### CSV Delay: Block-Based vs Time-Based

The server's unilateral-exit delay (`ArkServerInfo.UnilateralExit`) is a **BIP-68 relative timelock**, not a block count. arkd advertises a single integer that it overloads: below 512 it means blocks, 512 or above it means seconds (arkd's production default is `86400`, i.e. 24 hours). The SDK decodes that into an `NBitcoin.Sequence`, and the two encodings mature under completely different rules:

| `LockType` | Delay read from | Matures when |
| --- | --- | --- |
| `SequenceLockType.Height` | `Sequence.LockHeight` (blocks) | tip height ≥ leaf confirmation height + delay |
| `SequenceLockType.Time` | `Sequence.LockPeriod` (512-second units) | tip **median time past** ≥ leaf block's MTP + delay |

Never do arithmetic on the raw `Sequence.Value`: a time-based lock sets `SEQUENCE_LOCKTIME_TYPE_FLAG` (bit 22), so a 24-hour delay reads as `4194472`. Branch on `LockType`, or use the SDK's helper:

```csharp
var maturity = await CsvMaturity.EvaluateAsync(
    serverInfo.UnilateralExit,
    leafConfirmationHeight,
    blockchain,
    ct);

if (maturity.IsMatured) { /* claim */ }
else logger.LogDebug("CSV not matured: {Progress}", maturity.Progress);
```

`UnilateralExitService` uses this internally for both the stateful and stateless paths, so no extra wiring is needed for the built-in flow. Two requirements fall out of it for custom `IBitcoinBlockchain` implementations:

- `GetChainTime` must return the tip's **median time past** (BIP 113) as `TimeHeight.Timestamp`, not the tip block's own `nTime`.
- `GetMedianTimePastAsync(blockHeight)` must resolve a historical block's MTP. It has a default implementation that throws `NotSupportedException`, so a backend that skips it fails loudly on a time-based delay instead of stalling. All three built-in backends (`NBXplorerBlockchain`, `EsploraBlockchain`, `RpcBlockchain`) implement it.

### Virtual Tx Storage Modes

- **Lite mode (default)**: Stores only txids + expiry. Fetches hex on demand when exit is actually started (saves storage, slower exit start). Right default for most wallets — the common case never exits unilaterally.
- **Full mode**: Fetches and stores raw tx hex on VTXO receive. Ready for instant exit without any indexer round-trip. Opt in via `opts.DefaultMode = VirtualTxMode.Full` when offline-exit capability is a hard requirement.

### No-Storage Modes

Two ways to use unilateral exit without paying the EF Core schema cost:

**1. In-memory storage** — same code paths as the durable flow (idempotent re-invocation, watchtower visibility) but state is held in `ConcurrentDictionary`s and lost on process restart. Right for recovery tooling, plugins, or ephemeral wallets that don't need cross-restart resume.

```csharp
services.AddUnilateralExit();
services.AddInMemoryExitStorage();  // registers InMemoryExitSessionStorage + InMemoryVirtualTxStorage
// Don't call ConfigureArkExitEntities() — no SQL tables needed
```

**2. Stateless API** — `UnilateralExitService.BroadcastExitChainAsync` + `ClaimMaturedExitAsync` skip both `IExitSessionStorage` and `IVirtualTxStorage` entirely. The SDK persists nothing exit-specific; the caller saves the returned `ExitPlan` record however they want and feeds it back to claim once the CSV timelock matures.

`BroadcastExitChainAsync` broadcasts **at most one** not-yet-onchain tx per call, not the whole chain — call it repeatedly (like `ProgressExitsAsync`) until the chain is fully confirmed. This is required by Bitcoin Core's TRUC/v3 relay policy (BIP 431): each tx gets its own CPFP child to pay its zero fee, and a v3 tx can have at most 1 unconfirmed descendant, so the next tx in the chain can't be broadcast until the previous one confirms on-chain (the same constraint go-sdk and ts-sdk's unroll sessions are built around).

```csharp
// Call on an interval until the whole chain is confirmed (mirrors ProgressExitsAsync)
ExitPlan plan;
while (true)
{
    plan = await exitService.BroadcastExitChainAsync(
        walletId, vtxoOutpoint, claimAddress, ct);
    var leafStatus = await blockchain.GetTxStatusAsync(uint256.Parse(plan.LeafTxid), ct);
    if (leafStatus.Confirmed) break;
    await Task.Delay(pollInterval, ct);
}

// ... persist `plan` somewhere (a JSON blob, a settings entry, etc.) ...

// Later — once the CSV timelock matures:
var claimTxid = await exitService.ClaimMaturedExitAsync(plan, ct);
if (claimTxid is null)
{
    // CSV not yet matured; try again later
}
```

Trade-off vs. the stateful path: no idempotency (re-broadcasting an already-confirmed link is a no-op, but the caller must track when to stop polling), no automatic watchtower progression. The caller owns persistence and time-keeping in their own format.

Virtual tx data is automatically pruned when VTXOs are spent. Sibling VTXOs sharing internal tree nodes naturally deduplicate — shared nodes are only cleaned up when no VTXO references them.

## Contracts

Derive receiving addresses and manage contracts:

```csharp
// Derive a new receive contract (generates a new Arkade address)
var contract = await contractService.DeriveContract(
    walletId,
    NextContractPurpose.Receive);

// The contract's script can be converted to an ArkAddress for display
```

### Contract scope (onchain / offchain)

Every contract type declares which layer(s) its funds live on via
`ArkContract.DefaultScope`, a `[Flags] ContractScope` (`Onchain`, `Offchain`, or
both). Boarding contracts are `Onchain`; payment, VHTLC, delegate, hash-lock,
note and unknown contracts are `Offchain`. This replaces ad-hoc
`Type == "Boarding"` checks — sync, sweep and recovery ask the scope instead.

The resolved scope is persisted on each contract (`ArkContractEntity.Scope`) and
is SQL-queryable. Filter by scope through `IContractStorage.GetContracts`:

```csharp
// On-chain contracts to poll/sweep (boarding UTXOs, etc.)
var onchain = await contractStorage.GetContracts(scope: ContractScope.Onchain);

// Off-chain contracts (VTXOs) — also matches dual-scope contracts
var offchain = await contractStorage.GetContracts(scope: ContractScope.Offchain);
```

The filter translates to a SQL bitwise predicate, so a dual-scope contract
(`Onchain | Offchain`) is returned by both queries. A per-instance override can
be supplied at persistence time via `ArkContract.ToEntity(scopeOverride: …)`;
when omitted, the type's `DefaultScope` is used.

> EF Core note: query scope with the bitwise form (the SDK does this internally);
> `Enum.HasFlag` does **not** translate to SQL. The `Scope` column ships via the
> entity configuration — consumers that manage their own schema with EF
> migrations should add a migration that creates the column (default `Offchain`)
> and backfills existing boarding rows to `Onchain`.

## HD Wallet Recovery

When importing an HD wallet from its mnemonic, the SDK has no record of contracts the previous instance derived. `HdWalletRecoveryService` rebuilds that state by sweeping derivation indices via gap-limit and asking each registered `IContractDiscoveryProvider` whether it ever saw activity at that index.

The default providers ship with the SDK:

- `IndexerVtxoDiscoveryProvider` (`AddArkCoreServices`) — asks arkd's indexer for VTXOs at the index's payment script.
- `BoardingUtxoDiscoveryProvider` (`AddArkCoreServices`, opt-in via registering an `IBitcoinBlockchain` whose `GetUtxosAsync` is implemented — NBXplorer or Esplora) — asks for historical UTXOs at the index's boarding address.
- `BoltzSwapDiscoveryProvider` (`AddArkSwapServices`) — asks Boltz `/v2/swap/restore` whether the index's user pubkey ever participated in a swap.

```csharp
var recovery = serviceProvider.GetRequiredService<HdWalletRecoveryService>();

var report = await recovery.ScanAsync(walletId);
// or with options:
var deepReport = await recovery.ScanAsync(walletId, new RecoveryOptions(GapLimit: 50));

Console.WriteLine($"Highest used index: {report.HighestUsedIndex}");
Console.WriteLine($"Discovered {report.DiscoveredContracts.Count} contract(s)");
```

Custom discovery sources are added by implementing `IContractDiscoveryProvider` and registering it in DI; the orchestrator picks them up automatically. See [docs/articles/recovery.md](docs/articles/recovery.md) for the full API and tuning guidance.

## Arkade Signer-Key Rotation

When an Arkade server operator rotates its signing key, the SDK handles re-enrollment automatically — no consumer code required. Three regimes apply depending on when the rotation is detected relative to the cutoff:

| Regime | Condition | Handled by |
|--------|-----------|------------|
| Collaborative sweep | Before cutoff (or no cutoff) | `ServerKeyRotationSweepPolicy` re-enrolls VTXOs under the current signer via the sweeper |
| Wait | After cutoff, before VTXO expiry | `CanSpendOffchain` **and** `SimpleIntentScheduler` exclude the coin — it still needs a forfeit the operator won't co-sign — until it is forfeit-free (swept/unrolled) |
| Recovery re-enroll | Expired VTXO | Intent scheduler; batch session skips forfeit so the old key is not needed |

`ContractReconciliationService` keeps every SingleKey wallet's "Default" receive contract aligned with the current signer. It triggers automatically on startup, `WalletSaved`, and `ServerInfoChanged` — no extra registration needed beyond `AddArkCoreServices`.

To react to a rotation event in your own service, inject `IServerInfoCacheInvalidation`:

```csharp
public class MyService(IServerInfoCacheInvalidation serverInfoCache)
{
    public void Start() =>
        serverInfoCache.ServerInfoChanged += (_, e) =>
            Console.WriteLine($"Arkade server info changed: {e.Reason}");
}
```

To react when a wallet's sweep destination is auto-disabled because the signer it was keyed to was rotated away, inject `IDestinationSafetyNotifier`:

```csharp
public class MyService(IDestinationSafetyNotifier destinationSafety)
{
    public void Start() =>
        destinationSafety.DestinationDisabled += (_, e) =>
            Console.WriteLine($"Destination disabled for wallet {e.WalletId}: " +
                $"address {e.Destination} was keyed to deprecated signer {e.DeprecatedServerKey}. " +
                "Ask the user to confirm a new sweep destination.");
}
```

`IDestinationSafetyNotifier` is DI-aliased to the same `ContractReconciliationService` singleton that performs detection, so no extra registration is needed — just inject the interface. While the destination is flagged the SDK automatically routes swept funds to a self-output instead of the stale address; the destination resumes once the user re-confirms a fresh one.

See [docs/articles/signer-rotation.md](docs/articles/signer-rotation.md) for the full rotation model, detection paths, and version/digest header details.

## Pending Arkade Transaction Recovery

Arkade offchain transactions are a two-phase **Submit → Finalize** flow. If the process crashes between phases, the server holds the inputs as in-flight. It only allows the original pending tx to finalize. Without recovery, those coins stay stuck.

`PendingArkTransactionRecoveryService` reconciles this on every host startup. It pulls the server's view of pending transactions for each wallet, signs the checkpoint PSBTs locally, and finalizes them. It's registered automatically by `AddArkCoreServices` and wired into `ArkHostedLifecycle`, so the hands-off path requires no extra setup.

For deterministic timing (e.g. immediately after a user unlock) call it explicitly per-wallet:

```csharp
var recovery = serviceProvider.GetRequiredService<PendingArkTransactionRecoveryService>();

var finalizedTxIds = await recovery.FinalizePendingArkTransactionsAsync(walletId, ct);
foreach (var txId in finalizedTxIds)
    Console.WriteLine($"Recovered & finalized pending tx {txId}");
```

A pending transaction comes entirely from the server, so it is authorized locally **before anything is signed**: every checkpoint must pay the spent input's full value into the checkpoint contract this wallet would itself have built, and the accompanying final ark tx must spend exactly those checkpoint outputs while still carrying this wallet's own signature over it. Anything else is rejected with `UnauthorizedPendingArkTransactionException` and never signed, so recovery only ever completes a spend the wallet itself built and signed. That exception means the *server* presented something unauthorized; failures rooted in local state (no matching local VTXO for a checkpoint input, or a coin whose contract names no server key) surface as `InvalidOperationException` instead and imply nothing about the server.

Per-tx failures are logged + raised on `RecoveryFailed` and the loop continues — one bad pending tx never blocks the rest, and the next host start retries any leftovers. Subscribe to surface a banner or telemetry:

```csharp
recovery.RecoveryFailed += (_, e) =>
    Logger.LogWarning("Recovery failed for {ArkTxId} on {WalletId}: {Error}",
        e.ArkTxId, e.WalletId, e.Exception.Message);
```

See [docs/articles/pending-tx-recovery.md](docs/articles/pending-tx-recovery.md) for the full flow, sequence diagram, and edge cases.

## EF Core Storage

`NArk.Storage.EfCore` provides ready-made storage implementations. It is **provider-agnostic** — no dependency on Npgsql or any specific database driver.

### DbContext Setup

In your `DbContext.OnModelCreating`, call `ConfigureArkEntities`:

```csharp
public class MyDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureArkEntities(opts =>
        {
            opts.Schema = "ark";           // default
            // opts.WalletsTable = "Wallets";   // all table names configurable
        });
    }
}
```

### Storage Options

`ArkStorageOptions` controls schema, table names, and provider-specific behavior:

```csharp
services.AddArkEfCoreStorage<MyDbContext>(opts =>
{
    opts.Schema = "my_schema";

    // PostgreSQL-specific text search on contract metadata
    opts.ContractSearchProvider = (query, searchText) =>
        query.Where(c => EF.Functions.ILike(c.Metadata, $"%{searchText}%"));
});
```

### SQLite consumers: enable `StoreDateTimeOffsetAsTicks`

EF Core's SQLite provider rejects `ORDER BY` on `DateTimeOffset` columns, which breaks every paged query in this SDK (`GetVtxos`, `GetContracts`, `GetIntents`, …). Set `StoreDateTimeOffsetAsTicks = true` in `ConfigureArkEntities` (and `ConfigureArkPaymentEntities` if you use payment tracking) to store these columns as INTEGER ticks instead — `ORDER BY` then works natively.

```csharp
modelBuilder.ConfigureArkEntities(opts => opts.StoreDateTimeOffsetAsTicks = true);
```

Off by default to preserve native column types for Postgres/MSSQL consumers. Trade-off: the round-trip strips the original timezone offset (reads back as UTC). See [docs/articles/storage.md](docs/articles/storage.md#sqlite-storedatetimeoffsetastick-opt-in) for migration paths and details.

### Entities

| Entity | Table | Primary Key |
|--------|-------|-------------|
| `ArkWalletEntity` | `Wallets` | `Id` |
| `ArkWalletContractEntity` | `WalletContracts` | `(Script, WalletId)` |
| `VtxoEntity` | `Vtxos` | `(TransactionId, TransactionOutputIndex)` |
| `ArkIntentEntity` | `Intents` | `IntentTxId` |
| `ArkIntentVtxoEntity` | `IntentVtxos` | `(IntentTxId, VtxoTransactionId, VtxoTransactionOutputIndex)` |
| `ArkSwapEntity` | `Swaps` | `(SwapId, WalletId)` |

Payment-tracking entities (`ArkPaymentEntity`, `ArkPaymentRequestEntity`) are opt-in — see [Payment Repository](#payment-repository) below.

## Payment Repository

The SDK includes an **opt-in** payment repository for tracking end-to-end payments — both outbound (sends) and inbound (payment requests). This replaces the need for consumers to build their own payment-to-protocol linkage.

### Opt-In Setup

Payment tracking is not wired up by `AddArkEfCoreStorage` / `ConfigureArkEntities` — consumers that don't need it carry no extra schema or services. To enable it, call both the DI and model extensions:

```csharp
// OnModelCreating — alongside ConfigureArkEntities
modelBuilder.ConfigureArkEntities(opts => opts.Schema = "ark");
modelBuilder.ConfigureArkPaymentEntities(opts => opts.Schema = "ark");

// DI — alongside AddArkEfCoreStorage
services.AddArkEfCoreStorage<MyDbContext>();
services.AddArkPaymentTracking();
```

`AddArkPaymentTracking` registers `IPaymentStorage`, `IPaymentRequestStorage`, and the `PaymentTrackingService` (as an `IHostedService`, so its event subscriptions activate on startup). After calling `ConfigureArkPaymentEntities`, add the corresponding EF Core migration so the `Payments` and `PaymentRequests` tables are created.

### Outbound Payments (`ArkPayment`)

Track a payment you're sending, linked to the protocol object that proves it:

```csharp
var payment = new ArkPayment(
    PaymentId: Guid.NewGuid().ToString(),
    WalletId: walletId,
    Recipient: "tark1q...",
    Amount: 50_000,
    Method: ArkPaymentMethod.ArkSend,
    Status: ArkPaymentStatus.Pending,
    FailReason: null,
    CreatedAt: DateTimeOffset.UtcNow,
    CompletedAt: null)
{
    IntentTxId = intentTxId // links to the Arkade intent
};

await paymentStorage.SavePayment(payment);

// Query payments
var pending = await paymentStorage.GetPayments(
    walletIds: [walletId],
    statuses: [ArkPaymentStatus.Pending]);
```

Payment methods: `ArkSend`, `CollaborativeExit`, `SubmarineSwap`, `ChainSwap`.
Proof fields: `IntentTxId` (Arkade sends), `SwapId` (swaps), `OnchainTxId` (collab exits).

### Inbound Payment Requests (`ArkPaymentRequest`)

Generate a payment request with multiple payment options:

```csharp
var request = new ArkPaymentRequest(
    RequestId: Guid.NewGuid().ToString(),
    WalletId: walletId,
    Amount: 100_000,             // null = any amount (donation-style)
    Description: "Order #1234",
    Status: ArkPaymentRequestStatus.Pending,
    ReceivedAmount: 0,
    CreatedAt: DateTimeOffset.UtcNow,
    ExpiresAt: DateTimeOffset.UtcNow.AddHours(1))
{
    ArkAddress = "tark1q...",
    BoardingAddress = "bcrt1p...",
    LightningInvoice = "lnbcrt...",
    ContractScripts = [arkScript, boardingScript], // scripts to watch
    SwapId = reverseSwapId                          // if Lightning enabled
};

await paymentRequestStorage.SavePaymentRequest(request);

// Look up by script (for matching incoming VTXOs)
var matched = await paymentRequestStorage.GetPaymentRequestByScript(vtxoScript);
```

### Automatic Status Tracking (`PaymentTrackingService`)

The `PaymentTrackingService` subscribes to `VtxosChanged`, `IntentChanged`, and `SwapsChanged` events and automatically updates payment statuses:

- **Outbound**: When an intent succeeds/fails or a swap settles/fails, the linked `ArkPayment` moves to `Completed` or `Failed`.
- **Inbound**: When a VTXO arrives on a watched contract script, the `ArkPaymentRequest` accumulates `ReceivedAmount` and transitions to `Paid` (or `PartiallyPaid` for fixed-amount requests). Overpayment is tracked in the `Overpayment` property.

It is registered by `AddArkPaymentTracking()` (see [Opt-In Setup](#opt-in-setup) above) and runs as an `IHostedService`, so its event subscriptions activate automatically on application startup — no manual resolution needed.

### Fulfillment Rules

- **Any-amount requests** (`Amount = null`): `Paid` immediately on first funds received.
- **Fixed-amount requests**: `Paid` when `ReceivedAmount >= Amount`. No underpayment tolerance.
- **Overpayment**: Tracked via `ArkPaymentRequest.Overpayment` (sats above the target). Status is still `Paid`.
- **Expiration**: Handled externally (timer/cron), not by the tracking service.

## Networks

Pre-configured network environments:

```csharp
// Fluent builder
builder.AddArk().OnMainnet();
builder.AddArk().OnMutinynet();
builder.AddArk().OnRegtest();
builder.AddArk().OnCustomGrpcArk("http://my-ark-server:7070");

// IServiceCollection
services.AddArkNetwork(ArkNetworkConfig.Mainnet);
services.AddArkNetwork(new ArkNetworkConfig(
    ArkUri: "http://my-ark-server:7070",
    BoltzUri: "http://my-boltz:9069/"));
```

## Swaps

The swap framework is **multi-provider** — swap providers are pluggable via DI and the `SwapsManagementService` routes operations to the right provider based on the requested asset pair.

### Concepts

A **swap route** is a directional asset pair:

```csharp
// Route = source asset → destination asset
var route = new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc);  // Lightning → Arkade
var route = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);    // Arkade → BTC onchain

// Arkade-issued assets
var myToken = SwapAsset.ArkAsset("asset1abc...");
```

Each `ISwapProvider` declares which routes it supports. The router resolves the correct provider for a given route automatically.

### Registration

```csharp
// Default: core services + Boltz (backward-compatible)
services.AddArkSwapServices();
```

Or register providers individually:

```csharp
// Core services only (no providers)
services.AddSingleton<SwapsManagementService>();
services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();
services.AddSingleton<IContractTransformer, VHTLCContractTransformer>();

// Pick your providers
services.AddBoltzProvider(opts => opts.BoltzUrl = "https://api.boltz.exchange");
```

### Route Discovery

Query which routes are available across all registered providers:

```csharp
var swaps = serviceProvider.GetRequiredService<SwapsManagementService>();

// All routes from all providers
var routes = await swaps.GetAvailableRoutesAsync(ct);
// e.g. [Lightning→Arkade, Arkade→Lightning, BTC→Arkade, Arkade→BTC, ...]
```

### Pricing

Get limits and quotes — the router picks the right provider:

```csharp
var route = new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc);

var limits = await swaps.GetLimitsAsync(route, ct);
// limits.MinAmount, limits.MaxAmount, limits.FeePercentage, limits.MinerFee

var quote = await swaps.GetQuoteAsync(route, amount: 100_000, ct);
// quote.SourceAmount, quote.DestinationAmount, quote.TotalFees, quote.ExchangeRate
```

### Executing a Reverse Swap (receive Lightning into Arkade)

`InitiateReverseSwap` creates the Boltz reverse swap and returns the BOLT11 invoice to hand to the payer. The SDK watches the swap and materializes the VTXO automatically.

```csharp
var invoice = await swaps.InitiateReverseSwap(
    walletId,
    new CreateInvoiceParams(LightMoney.Satoshis(50_000), "Order #1234", TimeSpan.FromHours(1)),
    cancellationToken: ct);
```

#### Who pays the swap fee

An optional `ReverseSwapFeePayer` decides who absorbs the Boltz reverse-swap fee:

| Mode | Invoice amount | Receiver nets | Use when |
|------|----------------|---------------|----------|
| `Recipient` (default) | `requested` | `requested − fee` | The payer's wallet verifies the invoice equals the amount it chose to pay (LNURL-pay / LUD-06). The **only** compliant option for lightning-address / checkout flows. |
| `Sender` | `requested + fee` | `requested` | The payer is shown the invoice directly (e.g. a manual BOLT11 scan) and you want to receive an exact amount. **Not LUD-06-compliant** — the invoice no longer matches the requested amount, so LNURL/checkout wallets reject it. |

```csharp
// Merchant receives the exact amount; the payer covers the fee.
var invoice = await swaps.InitiateReverseSwap(
    walletId,
    new CreateInvoiceParams(LightMoney.Satoshis(50_000), "Top up", TimeSpan.FromHours(1)),
    ReverseSwapFeePayer.Sender,
    ct);
```

Either way the SDK stores the actual on-chain amount Boltz delivers as `ArkSwap.ExpectedAmount`, so claim, refund, and payment tracking match the VTXO that arrives.

### Providers

| Provider | Routes | Features |
|----------|--------|----------|
| **Boltz** | Arkade &harr; Lightning, Arkade &harr; BTC on-chain | Submarine/reverse swaps, chain swaps with renegotiation, MuSig2 cooperative claiming **and refunding** (both BTC and Arkade sides), VHTLC management, WebSocket status updates |

### Recovery (Renegotiation + Cooperative Refund)

When a chain swap can't settle as originally quoted — user funds the lockup with the wrong amount, an LN invoice times out, or Boltz expires the swap — the SDK handles recovery automatically inside `BoltzSwapProvider.PollSwapState`. No manual call is needed.

* **`transaction.lockupFailed`** → asks Boltz for a renegotiated quote via `GET/POST /v2/swap/chain/{id}/quote` and updates `ArkSwap.ExpectedAmount` if Boltz accepts.
* **`swap.expired` / `transaction.failed` / `transaction.refunded`** → cooperative refund: BTC→Arkade refunds the BTC lockup with MuSig2 (`/v2/swap/chain/{id}/refund`); Arkade→BTC refunds the Arkade VHTLC via `/v2/swap/chain/{id}/refund/ark`. Marks the swap `Refunded`.
* **`swap.expired` with no funds locked** → marked `Failed` (nothing to recover).

Subscribe to `ISwapStorage.SwapsChanged` to observe transitions. To surface a "recovery available" indicator without committing to a refund, use the read-only inspectors:

```csharp
// Single swap
var info = await swapMgr.InspectSwapRecoveryAsync(walletId, swapId);
if (info.Status == SwapRecoveryStatus.Recoverable)
    Console.WriteLine($"{info.AmountSats} sats stranded — recovery runs automatically");

// Bulk audit (e.g. after wallet restore)
var report = await swapMgr.ScanRecoverableSwapsAsync(walletId);
```

### Implementing a Custom Provider

Implement `ISwapProvider` and register it:

```csharp
public class MySwapProvider : ISwapProvider
{
    public string ProviderId => "myprovider";
    public string DisplayName => "My Swap Provider";

    public bool SupportsRoute(SwapRoute route) =>
        route == new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning);

    public Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct) => ...;
    public Task StartAsync(string walletId, CancellationToken ct) => ...;
    public Task StopAsync(CancellationToken ct) => ...;
    public Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct) => ...;
    public Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct) => ...;
    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Register
services.AddSingleton<ISwapProvider, MySwapProvider>();
```

The `SwapsManagementService` will automatically discover it and route matching requests to it.

## Lightning Corridors (`NArk.ArkadeIntents`)

A second route between Arkade and Lightning, alongside the Boltz integration above. Terms are
negotiated over **RFQ** with any solver serving the pair, and settle into a covenant swap contract
that neither side has to stay online for. Full details in
[docs/articles/lightning-corridors.md](docs/articles/lightning-corridors.md).

There is no accept message: **funding your own derivation is acceptance.** You derive the swap
contract locally, compare it against the solver's `lockup_address`, and fund only on a match — which
is what makes a wrong or hostile solver able to produce only an address you decline, never one that
traps your funds.

### Sending — pay a BOLT11 from an Arkade balance

```csharp
// One service over every corridor. Register it once and reach all of them through it.
var intents = new ArkadeIntentsService(
    assetSwaps, lightningSend, lightningReceive, intentStorage, vtxoStorage, TimeProvider.System);

var funded = await intents.SendToLightningAsync(
    walletId: "my-wallet",
    invoice: "lnbcrt500000n1p...",
    rfqTransport: new HttpRfqTransport(httpClient, new Uri("http://localhost:3000")));

// Or reach a solver that has no inbound port at all, which is how they run in production:
//   using var relay = new NostrRfqTransport(new Uri("wss://relay.example"), solverPubkey);

// Refund once the locktime passes, if it never filled. Yours to call whenever you want it
// back — `AdvanceAllAsync` will also sweep it, but it is not the only way in:
await intents.RefundLightningSendAsync(funded.RfqId);
```

### Receiving — be paid over Lightning, take delivery on Arkade

```csharp
var pending = await intents.ReceiveFromLightningAsync(
    walletId: "my-wallet",
    amountSats: 50_000,
    rfqTransport: rfqTransport,
    covclaimdPubKey: covclaimdPubKey);   // read live from covclaimd, never hardcoded

Console.WriteLine($"have the payer settle: {pending.Invoice}");

// Once the solver funds the lockup — the monitor moves the intent to Claimable:
await intents.ClaimLightningReceiveAsync(pending.RfqId);
```

On this corridor **you** choose the secret and send only its hash, plus a copy sealed to covclaimd
the solver cannot open. The solver funds the Arkade side before the payment it is owed has settled,
so a solver able to open that packet could settle the invoice without ever delivering.

Claiming publishes the preimage, which is also how the solver gets paid — an unclaimed swap is one
where it reclaims its lockup and the payer's money was never earned. The preimage is persisted
before the invoice goes out, since nothing can re-derive it afterwards.

### In the sample wallet

`samples/NArk.Wallet` runs both corridors in the browser — Send pays a BOLT11 or an LNURL address,
Receive mints an invoice, and the Swap page claims and refunds. It is the Boltz submarine and
reverse swaps this sample used to run, replaced; the Boltz chain swaps stay, having no intent
corridor yet.

The wiring is `Services/ArkadeLightningService.cs`, and all of it is one options object:

```csharp
builder.Services.AddSingleton(new ArkadeLightningOptions
{
    CovclaimdUrl = new Uri("http://…"),   // optional; see below
});
```

**No solver is named.** Which ones exist is answered by the public registry at runtime, and the
sample picks one advertising a Lightning corridor on its network. The one thing the registry cannot
answer is where to reach it: a market entry carries the solver's key but not its relays, so
`ArkadeLightningOptions.RelayUrl` supplies a default until the index carries transports. When the
registry lists no Lightning market, the Receive page says so instead of offering an option that
cannot work.

covclaimd is optional. Both corridors work without it; what it adds is a daemon that races the
wallet's own claim, so a funded receive is still collected while the browser tab is closed — worth
having, because the claim window is a couple of hours.

> **Both corridors settle end to end against a live solver.** `ArkadeLightningTests` in
> `NArk.Tests.End2End` drives each one through funding, fill and claim: on send the solver pays the
> invoice and takes the lockup with the preimage; on receive the payer settles a hold invoice, the
> solver funds Arkade, and our claim publishes the preimage that releases it.
>
> They are not part of CI — the solver is not in the regtest stack, so run them deliberately with
> `--filter TestCategory=LightningCorridors` and point `ARKADE_LN_SOLVER_URL` at a solver you
> started yourself. They also drive a Lightning node through `docker exec … lncli`
> (`ARKADE_LND_CONTAINER`, default `lnd`) to mint and pay the invoices.

Both corridors build the same eight-leaf `VHTLCv2Contract`. Because the contract is an agreement
about bytes with no wire versioning, the derivation is pinned to golden vectors generated from the
counterparty's own implementation — regenerate them whenever the solver moves to a newer
ts-sdk pin:

```bash
node NArk.Tests/ArkadeIntents/Fixtures/generate-covenant-vectors.mjs \
  <node-project-with-arkade-sdk> > NArk.Tests/ArkadeIntents/Fixtures/covenant_swap.json
dotnet test NArk.Tests --filter VHTLCv2ContractTests
```

## ArkadeScript & Emulator (`NArk.Arkade`)

The optional `NArk.Arkade` package adds client-side support for [ArkadeScript](https://github.com/arkade-os/emulator) — a Bitcoin-Script superset (40+ extension opcodes for transaction introspection, asset queries, EC operations, streaming SHA-256, …) that the [emulator](https://github.com/arkade-os/emulator) co-signs only when the script attached to an input passes validation.

> **Opcode table.** Byte values track the deployed Arkade VM in `arkade-os/emulator` (`pkg/arkade/opcode.go`) — the authority on what each byte executes as. They mostly match the ts-sdk `ARKADE_OP` table, but the two diverge on `0xd7`–`0xe2` (ts-sdk lists 64-bit arithmetic / scriptnum conversion; the emulator runs byte-string + EC ops such as `OP_NUM2BIN` / `OP_ECPAIRING`). The emulator wins, since it is what actually executes the script.

Install:

```bash
dotnet add package NArk.Arkade
```

Build a script and resolve the emulator-tweaked signing key:

```csharp
using NArk.Arkade.Crypto;
using NArk.Arkade.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

// Compose an ArkadeScript via the opcode enum + ASM helpers
var bytes = ArkadeScript.AsmToBytes(
    "OP_0 OP_INSPECTOUTPUTSCRIPTPUBKEY 1 OP_EQUALVERIFY deadbeef OP_EQUAL");

// GET /v1/info returns a compressed (33-byte hex) signerPubkey. Tweak it for the
// script above to get the x-only key the emulator co-signs that input with:
var info = await emulator.GetInfoAsync();
ECPubKey emulatorPubKey = ECPubKey.Create(Convert.FromHexString(info.SignerPubkey));
TaprootPubKey signingKey = ArkadeTweak.Tweak(emulatorPubKey, bytes);
```

Pin an ArkadeScript leaf to an `ArkContract`-based VTXO via the multisig wrapper:

```csharp
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;

protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
{
    // Augmented N+1-of-N+1: alice + bob + tweaked emulator key
    yield return new ArkadeNofNMultisigTapScript(
        arkadeScript: bytes,
        baseOwners: [aliceXOnly, bobXOnly],
        emulatorKeys: [emulatorPubKey]);

    // Plus your existing CSV / collab-path / etc. leaves alongside it
    yield return new UnilateralPathArkTapScript(...);
}
```

Build the emulator REST client through DI and submit intents / transactions for co-signing:

```csharp
using NArk.Arkade.Hosting;

// One-liner: registers the REST client AND the IBatchSessionExtension that
// transparently co-signs any batch with arkade-bound inputs.
services.AddArkadeEmulator(opts =>
    opts.ServerUrl = "http://localhost:7073");

// Or wire the REST client without batch integration, and inject manually:
services.AddEmulatorClient(opts =>
    opts.ServerUrl = "http://localhost:7073");

// Inject IEmulatorProvider and call:
var info   = await emulator.GetInfoAsync();              // GET  /v1/info  (signerPubkey + deprecatedSignerPubkeys)
var signed = await emulator.SubmitTxAsync(...);          // POST /v1/tx
var sig    = await emulator.SubmitIntentAsync(...);      // POST /v1/intent
var fin    = await emulator.SubmitFinalizationAsync(...);// POST /v1/finalization
var onchn  = await emulator.SubmitOnchainTxAsync(...);   // POST /v1/onchain-tx  (fully on-chain spends)
```

#### Previous-transaction fields (`prevarktx` / `prevouttx`)

Emulator `v0.0.7`+ requires each submitted input to carry the transaction that funded it, whether or not that input's ArkadeScript introspects a previous output. A submission missing it is rejected with `missing prevout tx for input N`.

For offchain spends (`/v1/tx`) this is automatic: `AddArkadeEmulator` registers an `IPrevArkTxProvider` and `ArkadeEmulatorSpendSubmitter` annotates the Arkade transaction just before submitting. The provider reads from `IVirtualTxStorage` when the wallet already holds the VTXO's branch, then arkd's indexer, then — for boarding and commitment parents the indexer cannot serve — `IBitcoinBlockchain.GetRawTransactionAsync`. So a spend usually costs no extra round-trip. The field belongs to the PSBT's `unknown` map, which no signature commits to, so it is attached after signing; an input already carrying one is left alone, since the emulator rejects an input bearing two.

To annotate a PSBT yourself — an intent proof, or a transaction you build outside the spend path:

```csharp
using NArk.Arkade.Emulator;

// Offchain Arkade transaction: needs its checkpoints, since the transaction attached to
// Arkade input i is the one funding that input's *checkpoint*, not the checkpoint itself.
await arkTx.AttachPrevArkTxsAsync(checkpoints, prevArkTxProvider);

// BIP322 intent proof: input 0 is the message input (the emulator synthesises its
// prevout); inputs 1..N each get the transaction that created their outpoint.
await intentProof.AttachIntentPrevArkTxsAsync(prevArkTxProvider);
```

Both throw `InvalidOperationException` naming the input index and txid when a previous transaction cannot be resolved, rather than letting the emulator reject the submission.

`/v1/onchain-tx` uses the sibling `prevouttx` field. Attach each input's previous transaction with `PsbtHelpers.SetArkFieldPrevoutTx(input, prevTx)` (key type `0xde`) before submitting, fetching it via `IBitcoinBlockchain.GetRawTransactionAsync`.

Or co-sign a PSBT inline once it carries the user's partial sigs:

```csharp
using NArk.Arkade.Emulator;

if (ArkadePsbtExtensions.RequiresEmulatorCoSigning(spendingCoins))
{
    // Append the EmulatorPacket OP_RETURN to the unsigned tx so the
    // server can find the script body for each arkade-bound input.
    var packetOutput = ArkadePsbtExtensions.BuildEmulatorOutput(spendingCoins);
    if (packetOutput is not null) tx.Outputs.Add(packetOutput);

    // ...sign locally, then merge the emulator's partial sigs:
    psbt = await psbt.CoSignWithEmulatorAsync(emulator);
}
```

The wire encoding for the emulator's OP_RETURN packet is exposed as `EmulatorPacket.Serialize` / `EmulatorPacket.Parse` for callers that need to read or write the TLV directly. Cross-SDK byte-equality is enforced by the unit tests against the canonical fixtures vendored from `arkade-os/emulator pkg/arkade/testdata/`.

## Extensibility Points

The SDK uses a pluggable architecture. Register your implementations for:

| Interface | Purpose | Default |
|-----------|---------|---------|
| `IVtxoStorage` | VTXO persistence | `EfCoreVtxoStorage` |
| `IContractStorage` | Contract persistence | `EfCoreContractStorage` |
| `IIntentStorage` | Intent persistence | `EfCoreIntentStorage` |
| `ISwapStorage` | Swap persistence | `EfCoreSwapStorage` |
| `ISwapProvider` | Swap provider (route-based) | `BoltzSwapProvider` |
| `IWalletStorage` | Wallet persistence | `EfCoreWalletStorage` |
| `IWalletProvider` | Wallet signer/address resolution | `DefaultWalletProvider` |
| `ISafetyService` | Distributed locking | *Must implement* |
| `IBitcoinBlockchain` | Chain time, UTXO lookup, broadcast, fee estimation | `NBXplorerBlockchain` / `EsploraBlockchain` / `RpcBlockchain` |
| `IFeeEstimator` | Transaction fee estimation | `DefaultFeeEstimator` |
| `ICoinSelector` | UTXO selection strategy | `DefaultCoinSelector` |
| `ISweepPolicy` | VTXO consolidation rules | Register zero or more |
| `IContractTransformer` | Custom contract &rarr; coin transforms | Register zero or more |
| `IEventHandler<T>` | React to batch/sweep/spend events | Register zero or more |

## Local Development

The SDK uses [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) for local orchestration with Docker containers (arkd, Bitcoin Core, Boltz, etc.):

```bash
cd NArk.AppHost
dotnet run
```

### Running Tests

```bash
# Unit tests
dotnet test NArk.Tests

# End-to-end tests (requires Docker)
dotnet test NArk.Tests.End2End
```

## License

[MIT](LICENSE)

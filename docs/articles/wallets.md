# Wallets

Wallets are stored in `IWalletStorage` and materialized as address/signing providers on demand by `IWalletProvider` (default: `DefaultWalletProvider`). Signing material (when present) lives in `ArkWalletInfo.Secret`.

`WalletType` selects the signing flavour:

| `WalletType` | `Secret` | `GetSignerAsync` returns | Use case |
| --- | --- | --- | --- |
| `HD` | BIP-39 mnemonic | `HierarchicalDeterministicWalletSigner` | Per-contract derivation, boarding support |
| `SingleKey` | Nostr `nsec` | `NSecWalletSigner` | Static key, simple integrations |
| `WatchOnly` | `null` / empty | `null` | Observe-only — addresses + VTXOs, no signing |
| `Remote` | `null` / empty | `RemoteArkadeWalletSigner` (proxy) | Signing proxied to `IRemoteSignerTransport` |

`ArkWalletInfo.Secret` MUST be non-null/non-empty for `HD` and `SingleKey`, and MUST be null/empty for `WatchOnly` and `Remote`.

## HD Wallets (BIP-39)

Created from a BIP-39 mnemonic. The SDK derives per-contract keys along BIP-86 style derivation (`m/86'/coin'/0'`), giving:

- Unique address per invoice (privacy)
- Boarding address support (on-chain → Arkade)
- Deterministic recovery from the mnemonic

## SingleKey Wallets

Created from a Nostr `nsec` (raw 32-byte secret). All operations use a single static key:

- Simpler setup
- No boarding address support
- Suitable for testing or lightweight integrations

## Watch-Only Wallets

Created from an account descriptor only — no signing material. The SDK can derive addresses, observe VTXOs, and report balances, but signing-dependent operations (batch participation, unilateral exits) throw a descriptive `InvalidOperationException`. Useful for accounting dashboards, paired devices where the signer lives elsewhere, or air-gapped setups.

The descriptor shape picks the address provider:

- `tr(<pubkey-hex>)` — single-key style, one reusable address
- `tr([<fingerprint>/<path>]<xpub>/0/*)` — HD style, new derivation per contract

## Remote-Signing Wallets

The wallet record holds no key material; signing is proxied to an `IRemoteSignerTransport` resolved from DI. The transport sees `walletId` on every call so a single transport instance can serve many wallets (e.g. a server-side signing service, an HWI bridge, a browser-extension wallet).

Implement `IRemoteSignerTransport` for your bridge, register it in DI alongside `IWalletProvider`, and store wallets with `WalletType = WalletType.Remote`. If a remote wallet is loaded but no transport is registered, `GetSignerAsync` throws with a clear error.

## Creating a Wallet

`WalletFactory.CreateWallet` is a static helper that inspects the secret and produces the right `ArkWalletInfo` record. Persist the resulting record via `IWalletStorage`:

```csharp
var serverInfo = await clientTransport.GetServerInfoAsync(ct);

// HD wallet (from a mnemonic). Destination is an optional sweep-to Ark address.
var hd = await WalletFactory.CreateWallet(
    walletSecret: mnemonic,
    destination: null,
    serverInfo: serverInfo,
    cancellationToken: ct);
await walletStorage.SaveWallet(hd, ct);

// SingleKey wallet (from a Nostr nsec).
var sk = await WalletFactory.CreateWallet(
    walletSecret: "nsec1...",
    destination: null,
    serverInfo: serverInfo,
    cancellationToken: ct);
await walletStorage.SaveWallet(sk, ct);

// Watch-only wallet (from an account descriptor — no signing material).
var watchOnly = await WalletFactory.CreateWatchOnlyWallet(
    accountDescriptor: "tr([abcd1234/86'/1'/0']tpub.../0/*)",
    destination: null,
    serverInfo: serverInfo,
    cancellationToken: ct);
await walletStorage.SaveWallet(watchOnly, ct);

// Remote-signing wallet (signing proxied to an IRemoteSignerTransport).
// Construct ArkWalletInfo directly with WalletType.Remote — the transport
// must be registered in DI before GetSignerAsync is called for this wallet.
var remote = new ArkWalletInfo(
    Id: "tr([abcd1234/86'/1'/0']tpub.../0/*)",
    Secret: null,
    Destination: null,
    WalletType: WalletType.Remote,
    AccountDescriptor: "tr([abcd1234/86'/1'/0']tpub.../0/*)",
    LastUsedIndex: 0);
await walletStorage.SaveWallet(remote, ct);
```

`ArkWalletInfo.Id` is the deterministic wallet identifier derived from the descriptor — two imports of the same seed produce the same `Id`.

## Implementing a Remote Signer

For `WalletType.Remote` wallets the SDK never sees private material; every signing call is forwarded to an `IRemoteSignerTransport` you register in DI. Mirror `IArkadeWalletSigner` with an extra `walletId` parameter on each method:

```csharp
public class HardwareSignerTransport : IRemoteSignerTransport
{
    public Task<ECPubKey> GetPubKeyAsync(string walletId, OutputDescriptor descriptor, CancellationToken ct)
        => _bridge.GetPubKeyAsync(walletId, descriptor.ToString(), ct);

    public Task<MusigPartialSignature> SignMusigAsync(string walletId, OutputDescriptor descriptor,
        MusigContext context, MusigPrivNonce nonce, CancellationToken ct)
        => _bridge.SignMusigAsync(walletId, descriptor.ToString(), context, nonce, ct);

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(string walletId, OutputDescriptor descriptor,
        uint256 hash, CancellationToken ct)
        => _bridge.SignAsync(walletId, descriptor.ToString(), hash, ct);

    public Task<MusigPrivNonce> GenerateNoncesAsync(string walletId, OutputDescriptor descriptor,
        MusigContext context, CancellationToken ct)
        => _bridge.GenerateNoncesAsync(walletId, descriptor.ToString(), context, ct);
}

services.AddSingleton<IRemoteSignerTransport, HardwareSignerTransport>();
```

`DefaultWalletProvider` accepts the transport as an optional constructor dependency — existing setups that don't use remote signing don't need to register one.

## Using a Wallet

`IWalletProvider` exposes wallets as address/signer providers:

```csharp
var provider = await walletProvider.GetAddressProviderAsync(walletId, ct)
    ?? throw new InvalidOperationException("Wallet not found");

// Provider gives you contracts / addresses; pair it with ContractService
// to derive and record the contract as a single operation.
```

## Contracts (Receiving Addresses)

Use `ContractService.DeriveContract` to produce a contract for a specific purpose, persist it, and return it:

```csharp
var contract = await contractService.DeriveContract(
    walletId,
    NextContractPurpose.Receive,
    ContractActivityState.AwaitingFundsBeforeDeactivate,
    metadata: new Dictionary<string, string> { ["Source"] = "invoice" },
    cancellationToken: ct);

var arkAddress = contract.GetArkAddress()
    .ToString(serverInfo.Network.ChainName == ChainName.Mainnet);  // tark1q... / ark1q...
```

`NextContractPurpose` values:

- `Receive` — new address for inbound VTXOs
- `Boarding` — on-chain address that can be boarded into Arkade (HD wallets only)
- `SendToSelf` — change / internal-use contract

See [Spending](spending.md) for how to send funds, and [Storage](storage.md) for how wallets and contracts are persisted.

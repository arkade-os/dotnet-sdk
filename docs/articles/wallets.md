# Wallets

Wallets are stored in `IWalletStorage` and materialized as address/signing providers on demand by `IWalletProvider` (default: `DefaultWalletProvider`).

Two orthogonal axes describe any wallet — keep them separate at every layer:

**1. Key-derivation flavour** (`WalletType`):

| `WalletType` | Script shape | Use case |
| --- | --- | --- |
| `SingleKey` | `tr(pubkey)` — one flat key | Static key, simple integrations |
| `HD` | `tr([fp/path]xpub/0/*)` — xpub-derived child set | Per-contract derivation, boarding support |

**2. Signing capability** — answered by `IWalletProvider.GetSignerAsync`, *not* by the data type:

| `ArkWalletInfo.Secret` | `IRemoteSignerTransport` claims it | `GetSignerAsync` returns | Capability |
| --- | --- | --- | --- |
| non-empty | — | `CompositeArkadeWalletSigner` with the matching local provider | sign locally |
| non-empty | yes | composite with both local + remote providers (local first) | hybrid |
| null / empty | yes (`KnowsWalletAsync` → `true`) | composite with one `RemoteTransportKeyProvider` | sign via transport |
| null / empty | no | `null` | watch-only |

Capability lives at the *provider* boundary, not as a tag on the wallet record. Any combination of the two axes is valid: a remote-signed `SingleKey`, a watch-only `HD`, an HD wallet with local view-key paths and a remote-signed spend path, etc.

### Signer composition

`IArkadeWalletSigner` is always a `CompositeArkadeWalletSigner` built from one or more
`IPrivateKeyProvider`s. Each provider answers `CanProvideAsync(descriptor)` and exposes the
signer operations rooted in the key it owns; the composite dispatches each call to the first
provider that claims the descriptor (order is significant — register local providers first).

Three providers ship by default:

| Provider | Source | `CanProvideAsync` |
| --- | --- | --- |
| `Bip39KeyProvider` | BIP-39 mnemonic | descriptor origin's master fingerprint matches |
| `NsecKeyProvider` | Nostr `nsec` (single key) | descriptor's x-only pubkey matches |
| `RemoteTransportKeyProvider` | `IRemoteSignerTransport` | `transport.KnowsWalletAsync(walletId)` |

`DefaultWalletProvider` builds this composition automatically from the wallet record. To
extend — e.g. plug a hardware wallet in alongside a local mnemonic — implement
`IPrivateKeyProvider` and register the composite manually (or replace `DefaultWalletProvider`).

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

## Watch-Only and Remote-Signed Wallets

Both are described by the same data shape — `Secret = null` on an otherwise normal `ArkWalletInfo` — and distinguished at runtime by `IWalletProvider.GetSignerAsync`:

- No `IRemoteSignerTransport` registered, or `KnowsWalletAsync(walletId)` returns `false` → `GetSignerAsync` returns `null`. Watch-only: addresses and VTXOs are observable, signing-dependent operations (batch participation, unilateral exits) throw a descriptive `InvalidOperationException`.
- An `IRemoteSignerTransport` is registered and claims the wallet → `GetSignerAsync` returns a `CompositeArkadeWalletSigner` wrapping a `RemoteTransportKeyProvider`. Every signing call is forwarded to the transport. The transport sees `walletId` on every call so one instance can serve many wallets (server-side signing service, HWI bridge, browser-extension wallet, …).

`WalletType` is independent: a watch-only HD wallet derives addresses from its xpub; a watch-only single-key wallet has one fixed address. Same for remote — derivation is whatever the descriptor encodes; the signer-source is whatever the transport claims.

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

// Watch-only OR remote-signed: same data shape — null Secret + the descriptor. Whether the
// wallet ends up watch-only or remote-signed is decided at GetSignerAsync time by whether an
// IRemoteSignerTransport is registered and claims this walletId (KnowsWalletAsync).
var nonLocal = await WalletFactory.CreateWatchOnlyWallet(
    accountDescriptor: "tr([abcd1234/86'/1'/0']tpub.../0/*)",
    destination: null,
    serverInfo: serverInfo,
    cancellationToken: ct);
await walletStorage.SaveWallet(nonLocal, ct);
```

`ArkWalletInfo.Id` is the deterministic wallet identifier derived from the descriptor — two imports of the same seed produce the same `Id`.

## Implementing a Remote Signer

For wallets whose `Secret` is null, the SDK never sees private material; every signing call is forwarded to an `IRemoteSignerTransport` you register in DI. The transport itself decides which wallets it can sign for via `KnowsWalletAsync` — wallets it doesn't claim fall through to watch-only.

Mirror `IArkadeWalletSigner` with an extra `walletId` parameter on each method, plus the `KnowsWalletAsync` probe:

```csharp
public class HardwareSignerTransport : IRemoteSignerTransport
{
    public Task<bool> KnowsWalletAsync(string walletId, CancellationToken ct)
        => _bridge.IsPairedAsync(walletId, ct);

    public Task<ECPubKey> GetPubKeyAsync(string walletId, OutputDescriptor descriptor, CancellationToken ct)
        => _bridge.GetPubKeyAsync(walletId, descriptor.ToString(), ct);

    public Task<MusigPartialSignature> SignMusigAsync(string walletId, OutputDescriptor descriptor,
        MusigContext context, string sessionId, CancellationToken ct)
        => _bridge.SignMusigAsync(walletId, descriptor.ToString(), context, sessionId, ct);

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(string walletId, OutputDescriptor descriptor,
        uint256 hash, CancellationToken ct)
        => _bridge.SignAsync(walletId, descriptor.ToString(), hash, ct);

    public Task<MusigPubNonce> GenerateNoncesAsync(string walletId, OutputDescriptor descriptor,
        MusigContext context, string sessionId, CancellationToken ct)
        => _bridge.GenerateNoncesAsync(walletId, descriptor.ToString(), context, sessionId, ct);
}

services.AddSingleton<IRemoteSignerTransport, HardwareSignerTransport>();
```

`DefaultWalletProvider` accepts the transport as an optional constructor dependency — existing setups that don't use remote signing don't need to register one.

### MuSig2 Nonce Lifecycle

The MuSig2 nonce flow keeps the secret half on the signer side: `GenerateNoncesAsync` retains the
secret nonce, indexed by `walletId` + a caller-supplied `sessionId`, and returns only the public
half. `SignMusigAsync` looks the secret up by the same `sessionId` and consumes it on use —
calling `SignMusigAsync` without a prior matching `GenerateNoncesAsync` throws.

`sessionId` must be unique per signing operation within the signer's scope. In batch participation,
`TreeSignerSession` passes each tree-node txid as the sessionId; for other flows the caller picks
something equally disambiguating. `MusigContext.AggregatePubKey` is *not* enough on its own —
multiple tree nodes can share cosigner set + tweak, so their contexts have identical aggregate
pubkeys but different sighashes. The sighash is internal to `MusigContext` and can't be observed
by the signer, so the disambiguator has to come from the caller.

Implementations need an eviction policy for abandoned nonces (TTL or bounded count) so the secret
nonce store does not grow unbounded if a caller generates a nonce but never signs. The in-process
`NsecKeyProvider` / `Bip39KeyProvider` rely on remove-on-consume; long-lived transport
implementations need to add a sweep.

`DefaultWalletProvider` caches signer instances per wallet so that the secret-nonce store on a
local provider survives between the `GenerateNonces` call and the matching `SignMusig` call. A
fresh signer per call would silently break MuSig2 signing — the second call would always find an
empty store.

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

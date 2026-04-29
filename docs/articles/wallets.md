# Wallets

Wallets are stored in `IWalletStorage` and materialized as address/signing providers on demand by `IWalletProvider` (default: `DefaultWalletProvider`). The secret material itself lives in `ArkWalletInfo.Secret`.

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
```

`ArkWalletInfo.Id` is the deterministic wallet identifier derived from the descriptor — two imports of the same seed produce the same `Id`.

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

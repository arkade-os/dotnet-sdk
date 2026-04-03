# Wallets

## HD Wallets (BIP-39)

```csharp
var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
var wallet = walletFactory.CreateHdWallet("my-wallet", mnemonic);
```

HD wallets derive unique keys per contract using BIP-44 style derivation, providing:
- Unique address per invoice (privacy)
- Boarding address support
- Deterministic recovery from mnemonic

## SingleKey Wallets

```csharp
var key = new Key(); // or parse from Nostr nsec
var wallet = walletFactory.CreateSingleKeyWallet("my-wallet", key);
```

SingleKey wallets use a single static key for all operations:
- Simpler setup
- No boarding address support
- Suitable for testing or lightweight use

## Wallet Provider

`IWalletProvider` manages wallet lifecycle:

```csharp
// Get a wallet
var wallet = await walletProvider.GetWalletAsync("wallet-id");

// List all wallets
var wallets = await walletProvider.GetWalletsAsync();
```

## Contracts (Receiving Addresses)

Derive a new contract to receive funds:

```csharp
var contract = await paymentService.DeriveContract(
    walletId,
    NextContractPurpose.Receive,
    cancellationToken: ct);

var arkAddress = contract.GetArkAddress(); // tark1q... or ark1q...
```

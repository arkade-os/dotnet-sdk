# Swaps (Lightning Integration)

The SDK integrates with [Boltz Exchange](https://boltz.exchange) for trustless Lightning swaps.

## Setup

```csharp
// Via Generic Host
.EnableSwaps()

// Via IServiceCollection
services.AddArkSwapServices();
```

Configure the Boltz endpoint:

```csharp
services.Configure<BoltzOptions>(opts =>
{
    opts.BoltzUrl = "https://your-boltz-instance.com";
});
```

## Submarine Swap (Ark → Lightning)

Pay a Lightning invoice from your Ark wallet:

```csharp
var swap = await swapService.CreateSubmarineSwap(
    walletId,
    bolt11Invoice,
    cancellationToken: ct);
```

## Reverse Swap (Lightning → Ark)

Receive a Lightning payment as a VTXO:

```csharp
var swap = await swapService.CreateReverseSwap(
    walletId,
    amountSats: 50_000,
    cancellationToken: ct);

// swap.Invoice — BOLT11 invoice for the payer
// The SDK monitors the swap and creates the VTXO automatically
```

## Chain Swap (BTC ↔ Ark)

Move funds between on-chain Bitcoin and Ark:

```csharp
// BTC → Ark
var swap = await swapService.CreateChainSwap(
    walletId,
    direction: ChainSwapDirection.BtcToArk,
    amountSats: 100_000,
    cancellationToken: ct);
```

## Swap Lifecycle

All swaps follow a state machine managed by `SwapsManagementService`:

| State | Meaning |
|---|---|
| **Created** | Swap initiated, waiting for funding |
| **Pending** | Funding detected, swap in progress |
| **Completed** | Swap successful |
| **Failed** | Swap failed or expired |
| **Refunded** | Funds returned after failure |

The SDK monitors swap state automatically and handles cooperative signing (MuSig2) for claim transactions.

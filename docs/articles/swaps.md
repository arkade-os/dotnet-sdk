# Swaps (Lightning & On-Chain Bridge)

The SDK integrates with [Boltz Exchange](https://boltz.exchange) for trustless submarine and chain swaps.

## Setup

```csharp
// Via Generic Host
builder.AddArk()
    .EnableSwaps()
    // or with a custom Boltz endpoint:
    .OnCustomBoltz("https://api.boltz.exchange", websocketUrl: null);

// Via IServiceCollection
services.AddArkSwapServices();
services.AddHttpClient<BoltzClient>();
```

Boltz URIs are configured as part of `ArkNetworkConfig`, not as a separate options class. To override the default, pass a custom `ArkNetworkConfig` to `AddArkNetwork`.

All swap orchestration — creation, state tracking, cooperative signing, and refunds — goes through `SwapsManagementService`.

## Submarine Swap (Arkade → Lightning)

Pay a Lightning invoice from your Arkade wallet:

```csharp
var invoice = BOLT11PaymentRequest.Parse(bolt11String, network);
var swapId = await swapsManagement.InitiateSubmarineSwap(
    walletId,
    invoice,
    autoPay: true,                // pay the HTLC immediately from wallet VTXOs
    cancellationToken);
```

If you need to defer the HTLC payment (e.g., show the user a quote first), pass `autoPay: false` and then call `PayExistingSubmarineSwap(walletId, swapId, ...)` once confirmed.

## Reverse Swap (Lightning → Arkade)

Receive a Lightning payment as a VTXO:

```csharp
var swapId = await swapsManagement.InitiateReverseSwap(
    walletId,
    new CreateInvoiceParams
    {
        AmountSats = 50_000,
        Description = "Top up",
    },
    cancellationToken);

// The user pays the BOLT11 invoice that Boltz provides on the swap.
// The SDK watches the swap state and materializes the VTXO automatically.
```

## Chain Swap (BTC ↔ Arkade)

**BTC → Arkade** (receive on-chain Bitcoin into an Arkade VTXO):

```csharp
var (btcAddress, swapId, expectedLockupSats) =
    await swapsManagement.InitiateBtcToArkChainSwap(
        walletId,
        amountSats: 100_000,
        cancellationToken);

// Send `expectedLockupSats` to `btcAddress`. The SDK handles the
// cooperative MuSig2 cross-sign once Boltz broadcasts the claim.
```

**Arkade → BTC** (withdraw to on-chain Bitcoin):

```csharp
var swapId = await swapsManagement.InitiateArkToBtcChainSwap(
    walletId,
    btcAddress: "bc1q...",
    amountSats: 100_000,
    cancellationToken);
```

## Swap Lifecycle

`SwapsManagementService` runs as a hosted background service. It:

- Maintains a websocket subscription to Boltz for active swaps
- Polls swap state periodically as a failsafe
- Monitors VTXO events to detect settlement from the Arkade side
- Performs cooperative MuSig2 signing (chain-swap cross-sign, refund co-sign)
- Records state transitions in `ISwapStorage`

Typical states recorded against each `ArkSwap`:

| State | Meaning |
|---|---|
| `pending` | Waiting for user/counterparty action |
| `active` | Boltz has accepted the swap, preimage lock is live |
| `completed` | Swap finalized |
| `failed` | Timed out or refunded |

### Startup Behavior

`SwapsManagementService.StartAsync` does not block on the Ark server — if arkd is unreachable it defers initialization to a background retry loop, so hosts start up even when arkd is locked/syncing. Swap operations that depend on server info will queue until readiness is achieved.

## Restore After Data Loss

`SwapsManagementService.RestoreSwaps(walletId, ...)` rebuilds local swap state from Boltz's `/v2/swap/restore` endpoint, using wallet keys to identify owned swaps. Useful after re-importing a wallet from a mnemonic or nsec.

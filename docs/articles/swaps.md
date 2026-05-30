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

### Deterministic preimages (for recoverable claims)

The preimages we generate for reverse and chain swaps are derived deterministically from the wallet's signing material so that a restored wallet can re-derive them and claim outstanding VHTLCs. The scheme:

```
message  = SHA-256( "Arkade-Boltz-Preimage-v1" || descriptor.ToString() || u32_le(index) )
sig      = BIP-340-Schnorr( descriptor_key, message, aux_rand=null )
preimage = SHA-256( sig )
```

The signed input bundles three things:

- **Tag** — domain-separates this signature from any other use of the descriptor's key. The string is intentionally protocol+provider scoped (`Arkade`+`Boltz`) rather than SDK-scoped, so an Arkade wallet implemented on top of any Arkade SDK (TypeScript, Go, Rust, .NET) can produce the same preimage from the same wallet material and recover swaps the .NET SDK created. Versioned (`-v1`) so a future scheme bump can ship as `-v2` while recovery still tries v1 for older swaps.
- **Descriptor** — scopes the preimage to the specific swap descriptor.
- **Index** — lets a caller derive multiple preimages from the same descriptor. Always `0` today; baked into v1 so recovery iteration is forward-compatible without a scheme bump.

BIP-340 with `aux_rand=null` is deterministic per `(key, message)`, so same `(wallet, descriptor, index)` always yields the same preimage. Recovery: when `RestoreSwaps` rediscovers a reverse swap, it re-derives the candidate preimage with `index=0`, verifies `SHA-256(candidate) == restored.PreimageHash`, and attaches it to the swap's metadata for the sweeper to claim. Hash mismatch (legacy random preimage, or wrong descriptor) leaves the preimage out; `EnrichReverseSwapPreimage` remains the manual fallback.

Watch-only wallets (no signer) fall back to a random preimage on create — they don't get the recovery story but they can still execute swaps until they pair a signer.

> **Remote signers and determinism.** `IRemoteSignerTransport.SignAsync` MUST use `aux_rand=null` for the recovery scheme to work end-to-end on remote-signed wallets. Implementations that randomise `aux_rand` (e.g. hardware-wallet side-channel hardening) will produce a different signature each call → different preimage → recovery silently fails. See the XML doc on `SignAsync`. Local sources (`Bip39SigningSource`, `NsecSigningSource`) already satisfy this.

## Chain Swap Recovery (Renegotiation + Cooperative Refund)

Chain swaps can fail to settle when the user funds the lockup with an amount that doesn't match Boltz's original quote, when an LN invoice times out, or when the swap window expires. The SDK handles these cases automatically inside the routine status-poll loop in `BoltzSwapProvider.PollSwapState`:

| Boltz status                  | SDK behaviour                                                                                                                                                                                                          |
|-------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `transaction.lockupFailed`    | Calls `GET /v2/swap/chain/{id}/quote` to obtain a renegotiated amount, accepts via `POST` if Boltz allows it, updates the local `ArkSwap.ExpectedAmount`, and lets the swap proceed. Falls through to refund on refusal. |
| `swap.expired`, `transaction.failed`, `transaction.refunded` | Triggers cooperative refund: BTC→ARK refunds the BTC lockup via MuSig2 with Boltz; ARK→BTC refunds the Ark VHTLC via Boltz's `POST /v2/swap/chain/{id}/refund/ark`. Marks the swap `Refunded` on success. |
| `swap.expired` with no funds  | If the lockup was never funded, the swap is marked `Failed` with no recovery work needed.                                                                                                                              |

No manual call required — the same routine poll that drives the happy-path claim flow drives recovery. Hosts can subscribe to `ISwapStorage.SwapsChanged` to observe the resulting state transitions (`Refunded`, `Failed`).

### Inspecting Recovery State

When you want to surface a "recovery available" indicator in a wallet UI without committing to a recovery transaction, use the read-only inspection helpers:

```csharp
// Single swap — refreshes VTXOs from arkd before reporting
var info = await swapMgr.InspectSwapRecoveryAsync(walletId, swapId);

if (info.Status == SwapRecoveryStatus.Recoverable)
{
    Console.WriteLine($"Swap {info.SwapId} has {info.AmountSats} sats stranded — recovery will run automatically.");
}

// Bulk audit — useful after a wallet restore
var report = await swapMgr.ScanRecoverableSwapsAsync(walletId);
foreach (var entry in report.Where(r => r.Status == SwapRecoveryStatus.Recoverable))
{
    Console.WriteLine($"  {entry.SwapId}: {entry.AmountSats} sats at {entry.VtxoCount} vtxo(s)");
}
```

`SwapRecoveryStatus` values: `SwapNotFound`, `StillPending`, `AlreadySettled`, `AlreadyRefunded`, `NoFunds`, `Recoverable`, `InspectionError`. These methods are side-effect-free — recovery itself happens automatically inside the provider's poll loop.

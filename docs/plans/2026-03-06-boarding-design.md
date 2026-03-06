# Boarding Support Design

## Overview

Boarding is how onchain BTC enters the Ark VTXO tree. A user sends BTC to a
boarding address (a P2TR address with a specific tapscript), then registers it
as an intent in a batch round, converting it into a VTXO.

The boarding address has the same script structure as a regular VTXO
(`DefaultVtxoScript`) but uses `boarding_exit_delay` instead of
`unilateral_exit_delay`, and the UTXO lives onchain rather than in a tree.

This design also covers **unrolled VTXOs** — VTXOs whose tree transaction was
broadcast onchain. Both boarding UTXOs and unrolled VTXOs share the same batch
behavior: skip forfeit txs, sign the commitment tx instead.

## Approach: Hybrid — Contract Model with Dedicated Tracking

Use the existing contract model for script construction and intent integration.
Add a dedicated `IBoardingUtxoProvider` for onchain UTXO tracking via
NBXplorer. Boarding/unrolled UTXOs are stored as `ArkVtxo` records with
`Unrolled = true` and flow through the existing intent pipeline.

## Boarding Address Script

Two tapscript leaves, identical to `ArkPaymentContract`:

**Leaf 0 — Collaborative path** (used during batch boarding):
```
<owner_pubkey> OP_CHECKSIGVERIFY <server_pubkey> OP_CHECKSIG
```

**Leaf 1 — Unilateral exit** (used for sweep after CSV expires):
```
<boarding_exit_delay> OP_CHECKSEQUENCEVERIFY OP_DROP <owner_pubkey> OP_CHECKSIG
```

Internal key: unspendable NUMS point (all spending via script tree).

## Components

### 1. `ArkBoardingContract` (`NArk.Core/Contracts/`)

Extends `ArkContract`. Same structure as `ArkPaymentContract` but:
- Uses `BoardingExit` delay from `ArkServerInfo`
- `Type = "Boarding"`
- Same two script paths: `CollaborativePath()`, `UnilateralPath()`

### 2. `BoardingContractTransformer` (`NArk.Core/Transformers/`)

Implements `IContractTransformer`. Transforms boarding VTXOs into `ArkCoin`:
- `CanTransform()` — checks contract is `ArkBoardingContract` and user key is ours
- `Transform()` — returns `ArkCoin` with:
  - `SpendingScriptBuilder = boardingContract.CollaborativePath()`
  - `Swept = false`
  - `Unrolled = true` (from `ArkVtxo.Unrolled`)

### 3. Address Generation

`IArkadeAddressProvider` gets a new method:
- `GetBoardingContract()` — creates `ArkBoardingContract` using a fresh user
  descriptor, server signer key, and `boarding_exit_delay`
- Each call produces a unique boarding address (fresh key per deposit for privacy)
- Contract is persisted to `IContractStorage`

### 4. Boarding UTXO Provider (`BoardingUtxoSyncService`)

Background service using NBXplorer integration:
1. Queries `IContractStorage` for boarding contracts
2. For each boarding contract's script, polls NBXplorer for UTXOs at that address
3. When a **confirmed** UTXO is found, writes to `IVtxoStorage` as `ArkVtxo` with:
   - `Unrolled = true`
   - `Swept = false`
   - `ExpiresAt = confirmationTime + boardingExitDelay`
   - Script = boarding contract's script hex
4. When a UTXO disappears (spent onchain), marks as spent in storage

### 5. Intent Pipeline (no changes needed)

Once the boarding UTXO is in `IVtxoStorage`:
- `IntentGenerationService.RunIntentGenerationCycle` picks it up via `vtxoStorage.GetVtxos()`
- Matches to boarding contract in `IContractStorage`
- `CoinService.GetCoin()` dispatches to `BoardingContractTransformer`
- `IIntentScheduler` bundles into `ArkIntentSpec`
- Intent generated and registered — zero changes to `IntentGenerationService`

## Batch Session Changes

### `ArkCoin` — add `Unrolled` property

Propagated from `ArkVtxo.Unrolled` through the transformer.

```csharp
public bool Unrolled { get; }
public bool RequiresForfeit() => !Swept && !Unrolled;
```

### `SubmitSignedForfeitTxsRequest` — add `SignedCommitmentTx`

The proto already defines `signed_commitment_tx` at field 2. Add to C# record:

```csharp
public record SubmitSignedForfeitTxsRequest(string[] SignedForfeitTxs, string? SignedCommitmentTx = null);
```

### `HandleBatchFinalizationAsync` changes

1. **Forfeit loop**: coins with `Unrolled = true` skip via `RequiresForfeit() = false`
2. **New step — sign commitment tx**: after forfeit loop, if any `ins` have `Unrolled = true`:
   - Parse commitment PSBT from `BatchFinalizationEvent.CommitmentTx`
   - For each unrolled coin, find its input in the PSBT
   - Fill taproot leaf script (collaborative path) and sign with wallet signer
   - Submit signed PSBT as `SignedCommitmentTx` in `SubmitSignedForfeitTxsRequest`

### GrpcClientTransport

Update `SubmitSignedForfeitTxsAsync` to include `signed_commitment_tx` in the
gRPC request when present.

## Onchain UTXO Sweep (safety net)

Applies to **all** `Unrolled = true` VTXOs (boarding and unrolled alike):

- **Normal flow**: intent generation registers the UTXO in a batch ASAP via
  collaborative path. This is the primary mechanism.
- **CSV expires, UTXO still not in a batch**: the server rejects the intent
  (unilateral path is now spendable, making collaborative path unsafe). The
  only option is to sweep via the unilateral exit leaf to a fresh boarding
  address, restarting the CSV timer.
- **Sweep trigger**: after CSV has passed and UTXO is still unspent.
- **Default behavior**: auto-sweep back to a fresh boarding address.
- **Host override**: `IOnchainSweepHandler` interface allows the host app to
  override sweep behavior.
- **After sweep**: new boarding UTXO detected by `BoardingUtxoSyncService`,
  fresh CSV, re-enters pipeline.

## Server Constraints

From `GetInfoResponse`:
- `boarding_exit_delay` (field 9) — the CSV delay for boarding scripts
- `utxo_max_amount` (field 11) — `0` means boarding disabled, `-1` means no limit
- `utxo_min_amount` (field 10) — minimum boarding UTXO amount

The server validates during `RegisterIntent`:
- Boarding UTXO must be confirmed onchain
- CSV must not have expired yet
- Amount within configured min/max limits
- Cannot mix boarding inputs with onchain outputs in the same intent

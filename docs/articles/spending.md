# Spending

## Automatic Coin Selection

The simplest way to send — the SDK selects coins automatically:

```csharp
var txId = await spendingService.Spend(
    walletId,
    [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(50_000), recipientAddress)],
    cancellationToken);
```

The `DefaultCoinSelector` uses a greedy algorithm that:
1. Sorts coins by amount (descending)
2. Selects until target is met
3. Handles sub-dust change as OP_RETURN outputs
4. Falls back to adding more coins if change is sub-dust and OP_RETURN limit is reached

### Input Limit

The Arkade server rejects transactions whose weight exceeds its configured
`max_tx_weight` (`TX_TOO_LARGE`), so automatic coin selection is capped at
`ArkTransactionLimits.MaxVtxosPerArkTransaction` (50) inputs. If the target
amount cannot be covered within that cap — a wallet fragmented across many
small VTXOs — `Spend` throws `TooManyInputsException`. Wait for the intent
scheduler to consolidate the wallet's VTXOs (it batches them in chunks of the
same size), or send a smaller amount.

## Manual Coin Selection

For full control, specify inputs explicitly:

```csharp
var coins = await spendingService.GetAvailableCoins(walletId, ct);
var selected = coins.Where(c => c.Amount > Money.Satoshis(10_000)).ToArray();

var txId = await spendingService.Spend(
    walletId,
    selected,
    [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(50_000), recipientAddress)],
    cancellationToken);
```

`GetAvailableCoins` excludes on-chain VTXOs whose funding tx hasn't confirmed yet — today, boarding UTXOs still in the mempool (`ArkVtxo.IsUnconfirmedOnchain() == true`). They are not selectable because arkd rejects unconfirmed boarding inputs at settle time.

## Sub-Dust Outputs

Outputs below the dust threshold are converted to OP_RETURN outputs. The server configures the maximum number of OP_RETURN outputs per transaction (default: 3).

## Collaborative Exit (On-Chain Withdrawal)

To move funds from Arkade back to on-chain Bitcoin:

```csharp
var txId = await spendingService.Spend(
    walletId,
    [new ArkTxOut(ArkTxOutType.Onchain, Money.Satoshis(100_000), onchainAddress)],
    cancellationToken);
```

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

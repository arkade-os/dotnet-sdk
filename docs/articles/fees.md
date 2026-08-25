# Intent Fees

Every Arkade intent pays the operator a fee. The operator publishes that fee as a set of
[CEL](https://github.com/google/cel-spec) programs in its server info, and arkd re-evaluates
them when the intent is registered. If the intent's inputs minus its outputs fall short of
arkd's own number, registration fails with `INTENT_INSUFFICIENT_FEE`; if they exceed it, the
surplus stays with the operator. The SDK therefore has to predict arkd's number exactly, not
approximately.

`IFeeEstimator` is that prediction, and `DefaultFeeEstimator` is a deliberate mirror of arkd's
`arkFeeManager.ComputeIntentFees`.

## Estimating a fee

```csharp
// Registered by AddArk(...); inject it anywhere.
public class Quoting(IFeeEstimator feeEstimator)
{
    public async Task<Money> QuoteAsync(ArkCoin[] coins, ArkTxOut[] outputs)
    {
        // Money, not a bare satoshi count — no unit ambiguity at the call site.
        Money fee = await feeEstimator.EstimateFeeAsync(coins, outputs);
        return fee;
    }
}
```

An `ArkIntentSpec` overload prices a whole intent, which is what `IntentGenerationService` and
`SimpleIntentScheduler` use:

```csharp
var spec = new ArkIntentSpec(coins, outputs, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
Money fee = await feeEstimator.EstimateFeeAsync(spec);
```

## The operator's fee programs

`ArkServerInfo.FeeTerms` carries four programs. Each is evaluated once per input or output and
returns a `double` in satoshis:

| Term | Evaluated for | Variables |
|---|---|---|
| `IntentOffchainInput` | each VTXO input | `amount`, `expiry`, `birth`, `inputType`, `weight` |
| `IntentOnchainInput` | each boarding UTXO input | `amount` |
| `IntentOffchainOutput` | each VTXO output | `amount`, `script` |
| `IntentOnchainOutput` | each collaborative-exit output | `amount`, `script` |

All four environments also expose `now()`, returning the current unix time in seconds — a
time-based program such as `expiry - now() < 3600.0 ? 0.0 : 200.0` is evaluated the same way
client-side and server-side.

`inputType` is one of `'vtxo'`, `'recoverable'`, or `'note'`. A VTXO counts as `recoverable`
once the operator has **swept** it; merely passing its expiry does not change its type, which
matches how arkd classifies the input.

`expiry` and `birth` are unix seconds. A VTXO that expires by block height has no timestamp to
offer, and reports `0` rather than passing a block height off as a date.

## Rounding

arkd accumulates every term as a `float64` and rounds the **total** up once. The SDK does the
same, so a fee schedule with fractional terms — say `0.5` per input — costs 2 sat across three
inputs, not 3. Rounding each term separately would overpay on every multi-input intent.

## Dust

A fee is only payable if what is left clears the operator's dust threshold: arkd rejects a
sub-dust VTXO output with `AMOUNT_TOO_LOW`, and that rejection takes the whole intent — every
other coin batched into it — down with it. The SDK's schedulers check
`amount - fee >= serverInfo.Dust` and skip the affected coins rather than submitting an intent
that cannot succeed.

## Custom estimators

`IFeeEstimator` is registered as a singleton and can be replaced — for example to add a safety
margin over the operator's quote:

```csharp
public class PaddedFeeEstimator(DefaultFeeEstimator inner) : IFeeEstimator
{
    public async Task<Money> EstimateFeeAsync(
        ArkCoin[] coins, ArkTxOut[] outputs, CancellationToken cancellationToken = default) =>
        await inner.EstimateFeeAsync(coins, outputs, cancellationToken) + Money.Satoshis(10);

    public async Task<Money> EstimateFeeAsync(
        ArkIntentSpec spec, CancellationToken cancellationToken = default) =>
        await inner.EstimateFeeAsync(spec, cancellationToken) + Money.Satoshis(10);
}

services.AddSingleton<IFeeEstimator, PaddedFeeEstimator>();
```

Padding costs real satoshis on every intent — the operator keeps the surplus — so prefer it
only where a stale server-info cache could otherwise leave the estimate short.

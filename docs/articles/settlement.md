# Settlement (Threshold-Based Payouts)

Settlement moves value **out** of an Arkade wallet once its balance reaches a configured threshold — to another Arkade address, to an on-chain Bitcoin address, or to any destination your application knows how to reach.

It is a separate subsystem from the sweeper. `SweeperService` consolidates VTXOs back into the same wallet so they stay spendable; settlement pays them out.

## The three layers

| Layer | Question it answers | Contract |
| --- | --- | --- |
| Policy | *When should this wallet settle, and how much?* | `ISettlementPolicy` yields `SettlementPlan`s |
| Routing | *Which rail handles this destination?* | `CompositeSettlementService` picks the first available `ISettlementService` whose `CanSettle` accepts it |
| Rail | *How is the value actually moved?* | `ISettlementService.SettleAsync` |

`SettlementService` (a `BackgroundService`) joins them: it queues a wallet whenever its VTXOs change, an intent leaves a batch, or a registered `ISettlementTriggerSource` reports activity; consults every `ISettlementGate`; computes the available balance; asks the policies; and routes the resulting plan.

Each layer is replaceable on its own — a custom policy keeps the built-in rails, a custom rail keeps the built-in policy.

## Setup

```csharp
services.AddArkSettlement();
```

The engine is inert until you register an `ISettlementConfigProvider` that returns rules — the SDK persists no settlement configuration of its own, because your application already stores it.

```csharp
public class MySettlementConfigProvider(IMySettingsStore store) : ISettlementConfigProvider
{
    public async Task<IReadOnlyCollection<SettlementConfig>> GetConfigs(
        string? walletId = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await store.GetPayoutSettings(walletId, cancellationToken);

        return settings
            .Select(setting => new SettlementConfig(
                WalletId: setting.WalletId,
                Destination: SettlementDestination.Ark(setting.PayoutAddress),
                Threshold: setting.Threshold))
            .ToArray();
    }
}

services.AddSingleton<ISettlementConfigProvider, MySettlementConfigProvider>();
```

The threshold gates **when** a settlement fires, never **how much** moves: a wallet configured at 100 000 sats that reaches 250 000 settles all 250 000. Cap a single settlement with `SettlementConfig.MaxAmount` when a rail has an upper limit.

### What counts towards the threshold

Each rule measures exactly one denomination, named by `SettlementConfig.SourceAsset`: `SettlementAssets.Btc` by default, or the id of an Arkade-issued asset. Coins locked by a pending intent and coins past their expiry are out of both.

BTC and assets are then kept apart. An asset VTXO holds a dust-sized satoshi amount as the asset's carrier, not as spendable BTC — spending it for BTC would take the asset with it — so asset carriers land in `SettlementContext.AssetCoins` / `AssetBalances` and never in `AvailableCoins` / `AvailableBalanceSats`. A wallet holding nothing but assets never crosses a BTC threshold, and a BTC balance far above an asset rule's number never fires it.

## Settling an Arkade asset

```csharp
// Pay out USDT0 once the wallet holds 500 000 units of it, whatever its BTC balance is.
new SettlementConfig(
    WalletId: walletId,
    Destination: SettlementDestination.ArkAsset(payoutAddress, usdt0AssetId),
    Threshold: 500_000,
    SourceAsset: usdt0AssetId);
```

Thresholds, `MaxAmount`, `SettlementPlan.Amount`, and `SettlementRequest.Amount` are all denominated in the source asset's atomic units — satoshis only when that asset is BTC. Configure one rule per asset you settle; a rule never spills over into another denomination.

`ArkAssetSettlementService`, registered by `AddArkSettlement()`, is the rail for these. It sends the asset to an Arkade address, or consolidates it onto a freshly derived address of the settling wallet when the destination carries none, and it keeps the mechanics of an asset VTXO intact:

- the remainder of a partial settlement comes back as an **asset change output**, so the packet never shows more asset going in than coming out;
- each asset output is funded with one dust carrier, topped up from the wallet's BTC coins when the spent carriers alone do not cover them;
- the wallet's **auto-sweep destination** is not applied. That setting resolves every send-to-self to the configured consolidation address, which is what the sweeper and the intent scheduler rely on — but here it would send the asset there rather than where the rule points, and would move the very remainder a `MaxAmount` cap chose to keep for the next settlement.

The rail transfers, it does not convert: the asset leaving the wallet has to be the asset the destination expects, or it throws `SettlementNotSupportedException`. Settling USDT0 *into* something else — BTC, another stablecoin, an exchange balance — is a conversion, and that is one more `ISettlementService` (see [Adding a rail](#adding-a-rail)) whose `CanSettle` accepts the destination and which reads `SettlementRequest.SourceAsset` for what it is being handed.

## Destinations

A `SettlementDestination` is a network, an asset on that network, and an address:

```csharp
SettlementDestination.Ark("ark1…");              // Arkade address
SettlementDestination.ArkSelf();                 // back into the same wallet
SettlementDestination.BitcoinOnchain("bc1…");    // on-chain Bitcoin
new SettlementDestination("tron", "USDT", "TX…");     // your rail
new SettlementDestination("base", "USDC", "0x…");     // your rail
```

Network and asset are free-form strings, not enums. The SDK only defines the two it settles itself (`SettlementNetworks.Ark`, `SettlementNetworks.Bitcoin`); anything else — an EVM chain, a stablecoin network, an exchange deposit rail — is a string your application picks and a rail it registers. Nothing in the SDK has to learn about it.

## The built-in rail

`DestinationSweepSettlementService`, registered by `AddArkSettlement()`, sends the settlement amount to an Arkade address through `ISpendingService`, or derives a fresh address of the settling wallet when the destination is `ArkSelf()`.

It also handles on-chain Bitcoin destinations via a collaborative exit, but only when you opt in:

```csharp
services.AddArkSettlement(options => options.EnableCollaborativeExit = true);
```

That is off by default so an application that pays Bitcoin out its own way — a swap, an exchange withdrawal, a batching service — can register that rail without competing with the built-in one for the same destination.

Arkade-issued assets are not handled here: every amount on this rail is satoshis, and an asset-denominated request is rejected rather than spent as satoshis. `ArkAssetSettlementService` handles those instead.

## Adding a rail

A stablecoin payout, an EVM transfer, an exchange deposit — each is one `ISettlementService` registration:

```csharp
public class UsdtSettlementService(IMyStablecoinClient client) : ISettlementService
{
    public bool Available => client.IsConfigured;
    public string? UnavailableReason => Available ? null : "Stablecoin provider is not configured.";

    public bool CanSettle(SettlementDestination destination) =>
        destination.IsAsset("USDT") && !string.IsNullOrWhiteSpace(destination.Address);

    public async Task<SettlementResult> SettleAsync(
        SettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        // request.SourceAsset says what is leaving the wallet — BTC, or an Arkade asset id —
        // and request.Amount is denominated in it.
        var transfer = await client.Send(
            request.WalletId, request.SourceAsset, request.Amount,
            request.Destination.Address!, cancellationToken);

        return new SettlementResult(
            TransferId: transfer.Id,
            SourceAmount: request.Amount,
            DestinationAmountSats: transfer.NetSats,
            FeesPaidSats: transfer.FeeSats,
            DestinationAtomicAmount: transfer.AtomicUnits);
    }
}

services.AddSingleton<ISettlementService, UsdtSettlementService>();
```

Notes for rail authors:

- `CanSettle` sees the destination only. Check `SettlementRequest.SourceAsset` inside `SettleAsync` and reject what the rail cannot spend — a BTC-only rail handed an asset amount would move the wrong value.
- `CanSettle` must be cheap and side-effect free — it runs on every routing decision.
- Report a rail that is configured but temporarily unusable as `Available = false` with an `UnavailableReason`; routing skips it and the reason surfaces in the error when nothing else matches, instead of failing a settlement.
- Use `DestinationAtomicAmount` for destinations that are not denominated in satoshis.
- Throw from `SettleAsync` only when funds have **not** been committed. Once value has left the wallet, return a result — a throw makes the engine retry and can double-spend the balance.
- Report a fee the rail cannot know yet as `FeesPaidSats: null` rather than zero; zero reads as "free" in an application's accounting.
- Rails are tried in registration order, so register a specific rail before a broader one.

## Custom policies

Register another `ISettlementPolicy` to decide on something other than a balance threshold — a payout schedule, an expiry-driven sweep, a per-invoice rule.

A policy yields plans rather than returning one, the same way an `ISweepPolicy` yields coins. The engine executes the union of what every policy yields, in order — there is no single winner, so settling a balance across two destinations is just two yields.

```csharp
public class WeeklyPayoutPolicy(IMySchedule schedule) : ISettlementPolicy
{
    public async IAsyncEnumerable<SettlementPlan> EvaluateAsync(
        SettlementContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await schedule.IsDue(context.WalletId, cancellationToken))
            yield break;

        yield return new SettlementPlan(
            SettlementDestination.Ark(await schedule.GetDestination(context.WalletId)),
            context.AvailableBalanceSats);
    }
}
```

Plans are executed against a balance that shrinks as each one commits, so two policies that independently plan the whole balance do not over-spend the wallet — the second is skipped. A plan whose settlement *failed* commits nothing and leaves the balance to the plans behind it. The remainder is tracked per denomination, so an asset payout never eats into the satoshis a BTC rule is waiting on: set `SettlementPlan.SourceAsset` and read the matching balance with `context.GetAvailableBalance(asset)`.

The engine evaluates every queued wallet: a policy that ignores `ISettlementConfigProvider` and decides on its own needs no extra opt-in.

`SettlementContext` gives the policy the wallet's spendable coins, with coins locked by pending intents and coins past expiry already removed, plus the chain time they were computed against. `AvailableCoins` / `AvailableBalanceSats` are BTC only; `AssetCoins` / `AssetBalances` hold the asset carriers, and `GetAvailableBalance(asset)` / `GetAvailableCoins(asset)` read either through one call. Set `SettlementPlan.Coins` to pin the exact coins to spend — the settlement counterpart of the coins a sweep policy yields. The destination sweep honours it, while the collaborative-exit path rejects it because it performs its own coin selection and fee estimation.

## Retries, and what the engine does not guarantee

The engine serialises work per wallet: a background evaluation, a manual `SettleAsync`, and a second caller for the same wallet queue behind each other, so no two settlements read the same balance and both plan to spend it.

What it does **not** do is deduplicate across attempts. The heartbeat re-queues every configured wallet, which is the retry behind the event-driven path — and a settlement whose transaction was broadcast but whose result never came back (a transport error, a process stopped mid-flight) is indistinguishable from one that never ran. If the spent VTXO is not yet visible as spent, the rule fires again on the next beat.

The SDK cannot close that on its own: knowing an attempt is already in flight means persisting it, and settlement configuration and history belong to the application. Two hooks are the place to do it:

- record every attempt from `PostSettlementActionEvent`, which is raised for success and failure alike, keyed by `SettlementRequest.Reference` when you set one;
- register an `ISettlementGate` that blocks the wallet while your own record shows a settlement in flight.

A rail that can accept an idempotency key should take `Reference` as one. Note that a repeated amount to a repeated destination is not by itself a duplicate: with `MaxAmount` set, a rule legitimately fires again on the next evaluation until the balance drops below the threshold.

## Blocking settlement

Register an `ISettlementGate` to veto settlement for a wallet — a manual review hold, an in-flight payout of your own, a maintenance window, funds your subsystem has already committed but not yet spent:

```csharp
public class ManualHoldGate(IMyHolds holds) : ISettlementGate
{
    public Task<bool> IsBlockedAsync(string walletId, CancellationToken cancellationToken = default) =>
        holds.IsOnHold(walletId, cancellationToken);
}
```

Every gate is consulted before any policy runs.

## Extra triggers

The engine reacts to VTXO and intent changes on its own. Register an `ISettlementTriggerSource` to add a signal it cannot see — your own payout system settling, an external deposit landing:

```csharp
public class MyTriggerSource : ISettlementTriggerSource
{
    public event EventHandler<string>? WalletActivity;

    public void NotifyDeposit(string walletId) => WalletActivity?.Invoke(this, walletId);
}

services.AddSingleton<MyTriggerSource>();
services.AddSingleton<ISettlementTriggerSource>(sp => sp.GetRequiredService<MyTriggerSource>());
```

Raising it for an unconfigured wallet is harmless — queued wallets are deduplicated and unconfigured ones are skipped.

## Reacting to settlements

`PostSettlementActionEvent` is raised for every attempt, successful or failed:

```csharp
public class SettlementRecorder(IMyLedger ledger) : IEventHandler<PostSettlementActionEvent>
{
    public Task HandleAsync(PostSettlementActionEvent @event, CancellationToken cancellationToken = default) =>
        @event.State == ActionState.Successful
            ? ledger.RecordTransfer(@event.Request.WalletId, @event.Result!.TransferId, cancellationToken)
            : ledger.RecordFailure(@event.Request.WalletId, @event.FailReason, cancellationToken);
}

services.AddSingleton<IEventHandler<PostSettlementActionEvent>, SettlementRecorder>();
```

A failed settlement is not retried immediately — the engine's heartbeat re-queues every configured wallet (15 minutes by default), and the next wallet activity re-queues it sooner.

## Options

| Option | Default | Purpose |
| --- | --- | --- |
| `Debounce` | 250 ms | Collapses a burst of VTXO and intent changes into one evaluation. |
| `HeartbeatInterval` | 15 min | Re-queues every configured wallet; this is the retry behind the event-driven path. `TimeSpan.Zero` disables it. |
| `EnableCollaborativeExit` | `false` | Let the built-in rail settle on-chain Bitcoin destinations. |

## Settling on demand

`SettlementService.SettleAsync` runs a settlement immediately, bypassing policies and gates, while still using the same routing and raising `PostSettlementActionEvent`:

```csharp
var result = await settlementService.SettleAsync(
    new SettlementRequest(walletId, 250_000, SettlementDestination.Ark("ark1…")));
```

It returns `null` when the settlement failed; the failure is reported through the event.

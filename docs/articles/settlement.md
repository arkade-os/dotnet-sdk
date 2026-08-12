# Settlement (Threshold-Based Payouts)

Settlement moves value **out** of an Arkade wallet once its balance reaches a configured threshold — to another Arkade address, to an on-chain Bitcoin address, or to any destination your application knows how to reach.

It is a separate subsystem from the sweeper. `SweeperService` consolidates VTXOs back into the same wallet so they stay spendable; settlement pays them out.

## The three layers

| Layer | Question it answers | Contract |
| --- | --- | --- |
| Policy | *When should this wallet settle, and how much?* | `ISettlementPolicy` → `SettlementPlan?` |
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
                ThresholdSats: setting.ThresholdSats))
            .ToArray();
    }
}

services.AddSingleton<ISettlementConfigProvider, MySettlementConfigProvider>();
```

The threshold gates **when** a settlement fires, never **how much** moves: a wallet configured at 100 000 sats that reaches 250 000 settles all 250 000. Cap a single settlement with `SettlementConfig.MaxAmountSats` when a rail has an upper limit.

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

Arkade-issued assets are not handled here either: settlement amounts are denominated in satoshis, so an asset balance needs its own rail.

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
        var transfer = await client.Send(
            request.WalletId, request.AmountSats, request.Destination.Address!, cancellationToken);

        return new SettlementResult(
            TransferId: transfer.Id,
            SourceAmountSats: request.AmountSats,
            DestinationAmountSats: transfer.NetSats,
            FeesPaidSats: request.AmountSats - transfer.NetSats,
            DestinationAtomicAmount: transfer.AtomicUnits);
    }
}

services.AddSingleton<ISettlementService, UsdtSettlementService>();
```

Notes for rail authors:

- `CanSettle` must be cheap and side-effect free — it runs on every routing decision.
- Report a rail that is configured but temporarily unusable as `Available = false` with an `UnavailableReason`; routing skips it and the reason surfaces in the error when nothing else matches, instead of failing a settlement.
- Use `DestinationAtomicAmount` for destinations that are not denominated in satoshis.
- Throw from `SettleAsync` only when funds have **not** been committed. Once value has left the wallet, return a result — a throw makes the engine retry and can double-spend the balance.
- Rails are tried in registration order, so register a specific rail before a broader one.

## Custom policies

Register another `ISettlementPolicy` to decide on something other than a balance threshold — a payout schedule, an expiry-driven sweep, a per-invoice rule. The engine takes the plan with the lowest `Priority` among the policies that produced one.

```csharp
public class WeeklyPayoutPolicy(IMySchedule schedule) : ISettlementPolicy
{
    public async Task<SettlementPlan?> EvaluateAsync(
        SettlementContext context,
        CancellationToken cancellationToken = default)
    {
        if (!await schedule.IsDue(context.WalletId, cancellationToken))
            return null;

        return new SettlementPlan(
            SettlementDestination.Ark(await schedule.GetDestination(context.WalletId)),
            context.AvailableBalanceSats,
            Priority: -1);
    }
}
```

A policy that does not read `ISettlementConfigProvider` also needs `SettlementOptions.AlwaysEvaluatePolicies = true`; otherwise the engine skips wallets with no configured rule before it computes a balance.

`SettlementContext` gives the policy the wallet's spendable coins, with coins locked by pending intents and coins past expiry already removed, plus the chain time they were computed against. Set `SettlementPlan.Coins` to pin the exact coins to spend — the destination sweep honours it, while the collaborative-exit path rejects it because it performs its own coin selection and fee estimation.

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
| `AlwaysEvaluatePolicies` | `false` | Evaluate policies even for wallets with no rule from the config provider. |
| `EnableCollaborativeExit` | `false` | Let the built-in rail settle on-chain Bitcoin destinations. |

## Settling on demand

`SettlementService.SettleAsync` runs a settlement immediately, bypassing policies and gates, while still using the same routing and raising `PostSettlementActionEvent`:

```csharp
var result = await settlementService.SettleAsync(
    new SettlementRequest(walletId, 250_000, SettlementDestination.Ark("ark1…")));
```

It returns `null` when the settlement failed; the failure is reported through the event.

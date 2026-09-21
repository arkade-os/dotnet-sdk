# Arkade Asset Swaps (offers and RFQ)

Swapping Arkade sats for an Arkade-issued asset, or back. Both legs are on Arkade, so this is the
**atomic class**: there is no hash lock, no `refund_locktime` and no refund path, because the
covenant pays you in the same transaction that takes your deposit. The solver fills or nothing moves.

Two ways in, and the difference is who sets the price:

| | who names the terms | when it expires | how it is reclaimed |
| --- | --- | --- | --- |
| `CreateSwap` — a standing offer | you do | never | `CancelSwap` |
| `CreateQuotedSwap` — negotiated over RFQ | the solver does, bindingly | `valid_until` | `CancelSwap` |

Both fund the same covenant, so everything downstream — the monitor, the cancel path, restore — is
the same. What changes is whether you found out the price before or after committing to it.

## The covenant

One offer, three leaves:

| leaf | who can spend | when |
| --- | --- | --- |
| `fulfill` | the Arkade Service, under the covenant | only in a transaction paying your script the wanted amount |
| `cancel` | you + the Arkade Service | any time |
| `exit` | you alone | after the operator's own unilateral-exit delay |

`fulfill` is what makes the swap trustless for you: it is an ArkadeScript covenant that inspects the
spending transaction's first output and refuses unless it pays **your** witness program at least the
amount you asked for. A solver cannot take the deposit and deliver something else, or deliver to
somebody else, or deliver less.

The third leaf is why an offer is not a hostage to the operator. Both other paths need the Arkade
Service's signature, so without `exit` a server that went away would strand every unfilled deposit.
It is built by default at the operator's own advertised delay.

## A standing offer

You name the want amount, fund the covenant, and the offer is discoverable by any solver: the offer
itself travels inside the funding transaction as an Arkade extension packet, so nothing has to be
published anywhere else.

```csharp
var intent = await assetSwaps.CreateSwap(
    new CreateSwapRequest(
        WalletId: "my-wallet",
        Type: ArkadeSwapIntentType.BtcToAsset,
        DepositAmount: 50_000,        // sats
        WantAmount: 4_900,            // atomic units of the asset
        Asset: AssetId.FromString(assetIdHex)));
```

Nothing expires and nobody has agreed to anything, which is both the appeal and the risk: an offer
priced against a stale reading of the market is a free option somebody else will exercise. Price it
from a live market — `SolverDiscoveryService.FetchPriceAsync` plus `ComputeWantAmount` — or quote it
instead.

## Quoting first

`CreateQuotedSwap` asks a named solver what it will actually pay, checks the answer, derives the
offer covenant locally, and funds only its own derivation.

```csharp
var swap = await assetSwaps.CreateQuotedSwap(
    new QuotedSwapRequest(
        WalletId: "my-wallet",
        OfferAsset: null,                      // depositing sats
        WantAsset: AssetId.FromString(assetIdHex),
        Amount: 50_000,
        AmountSide: RfqAmountSide.From,
        MinToAmount: 4_900),                   // refuse a payout under this
    rfqTransport);

Console.WriteLine($"{swap.RfqId}: {swap.DepositedSats} sats → {swap.Quote.ToAtomicAmount} units");
```

The request carries two covenant parameters and nothing else: `maker_pk_script`, the taproot script
the fill must pay, and `maker_public_key`, the key that signs `cancel`. They come from your wallet
because they **are** you — the covenant pins the payout to that script, which is what makes the swap
trustless — and the solver's schema is strict about them, so a misspelling is refused rather than
quoted against a value it never read.

### Both amount sides

Unlike the EVM corridors, this one serves exact-out. `RfqAmountSide.From` asks "what will you pay for
this deposit"; `RfqAmountSide.To` asks "what deposit reaches this payout", and the solver resolves the
least input whose exact-in payout gets there.

Bound the side the **solver** chose, never the one you named — the named side comes back verbatim, so
asserting it proves nothing about the solver's pricing:

| you named | bound with |
| --- | --- |
| `From` (the deposit) | `MinToAmount` |
| `To` (the payout) | `MaxFromAmount` |

### The carrier

An Arkade asset rides on dust sats, and the quote publishes how many as `carrier_sats`. It is **not a
fee** and is already netted into both amounts: returned inside `to_amount` when your payout is sats,
charged out of `from_amount` when it is an asset. What it changes is what you actually send — an
asset deposit carries `from_amount` of the asset *plus* that many sats — and `CreateQuotedSwap` funds
with exactly the published number. A quote that publishes none falls back to the server's own dust
floor, which is the safe direction: a carrier under dust is refused outright.

### What is checked before funding

[`ArkadeSwapGates`](xref:NArk.ArkadeIntents.Assets.ArkadeSwapGates) is shorter than the HTLC
corridors' gates for a structural reason rather than an oversight. There is no timelock here, so
there is no headroom to check and no refund deadline to stay behind. What is left:

- the quote answers the market that was asked for;
- it has not lapsed, and carries positive amounts;
- the side you named came back unchanged, and the side the solver chose is within your bounds;
- the offer it published is, byte for byte, the one you derived — both the address your wallet sends
  to and the script the covenant compiles to.

The last throws
[`OfferAddressMismatchException`](xref:NArk.ArkadeIntents.Assets.OfferAddressMismatchException) and
nothing is funded. As on every other corridor, the quoted address is compared and never used.

## Reclaiming an unfilled offer

There is no refund path and nothing to wait for: `cancel` is a 2-of-2 of you and the Arkade Service,
available immediately.

```csharp
await assetSwaps.CancelSwap(intent.Id);
```

The intent moves to `Cancelling` before the spend so the monitor cannot read your own cancel as a
fill, and rolls back on failure. Note that the swap is keyed by its **funding txid**, quoted or not,
because that is what the cancel path looks the deposit up by; a quoted swap's negotiation id is on
the intent's metadata instead (`intent.RfqId()`).

## What the solver has to be running

The RFQ corridor is served from a market row the solver's operator configures — a price feed, each
leg's precision, the spread and the payout bounds. A solver that serves the packet path (taking
standing offers) does not necessarily quote this one, and the registry card is what says which
markets it prices.

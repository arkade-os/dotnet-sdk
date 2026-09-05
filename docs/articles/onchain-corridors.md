# Onchain Corridors

Two corridors between an Arkade balance and Bitcoin L1, negotiated over the same RFQ protocol and
settling into the same covenant as the [Lightning corridors](lightning-corridors.md):

| pair | direction | who funds first | client's recourse |
|---|---|---|---|
| `arkade:BTC->onchain:BTC` | off-board — Arkade balance out to L1 | the client, on Arkade | the Arkade covenant's `refundWithoutReceiver` |
| `onchain:BTC->arkade:BTC` | on-board — L1 sats into Arkade | the client, on L1 | the L1 HTLC's own refund leaf |

Both need an `IBitcoinBlockchain` registered. `AddArkadeIntentsServices()` wires the corridor only
when one is present, so a Lightning-only deployment is unaffected — and a caller that reaches for it
anyway gets an error naming what is missing rather than a null reference from inside a facade.

## The shape of the thing

Two contracts on two rails, linked by one secret.

- An **Arkade covenant** (`VHTLCv2Contract`), identical to the one both Lightning corridors use.
- A **Bitcoin L1 HTLC** (`OnchainHtlc`): a two-leaf taproot script — claim on the preimage, refund on
  a CLTV — with the BIP-341 NUMS point as its internal key, so there is no key-path spend and both
  ways out are visible to anyone holding the address.

Whoever funds first holds the secret. That is the whole trust model: the party that is exposed is the
party that can withhold the thing the other needs, so nothing is ever owed on trust.

There is no accept message on either corridor. **Funding your own derivation is acceptance.** Every
address is rebuilt locally from the quote's binding fields plus your own data and compared against
the solver's rendering of it; you fund only on a match. A wrong or hostile solver can therefore
produce only an address you decline, never one that traps your funds.

## The deadline ordering

This is the corridor's central safety property, and the one thing neither contract enforces. Each
rail has its own refund deadline, and their order decides whether the swap is safe.

**Off-board.** You fund Arkade, the solver funds L1, your L1 claim publishes the preimage, and the
solver takes the Arkade side with it. So your Arkade refund must open **last**:

```
now ──── htlc_locktime ──── (+2h margin) ──── refund_locktime
         solver's L1 way out                  your Arkade way out
```

Reversed, you could reclaim on Arkade while the solver could still reclaim on L1, and one leg pays
for both. `OnchainSendGates.AssertFundable` refuses a quote that gets it wrong, with
`TimelocksOutOfOrder`.

**On-board.** The mirror. You fund L1, the solver funds Arkade, your Arkade claim publishes the
preimage, and the solver takes the L1 side. So the solver's Arkade refund opens **first**:

```
now ──── refund_locktime ──── (+15m margin) ──── htlc_locktime
         solver's Arkade way out                 your L1 way out
```

`OnchainReceiveGates.AssertFundable` enforces this. It is deliberately stricter than the reference
client, which omits the check on this leg — the reference *solver* sizes its own Arkade refund as
`htlc_locktime` minus exactly that margin, so no honest quote is refused by checking it, and a quote
that fails it is one you could only complete by robbing the counterparty.

Both gates also refuse an expired quote, a confirmation count outside 1–6, too little headroom before
your own claim window closes, and an L1 deadline leaving no room to confirm and settle within.

## Off-boarding

```csharp
var funded = await intents.SendToOnchainAsync(
    walletId: "my-wallet",
    payoutAddress: BitcoinAddress.Create("bcrt1q...", Network.RegTest),
    amountSats: 50_000,
    rfqTransport: rfqTransport,
    amountSide: RfqAmountSide.To);      // pin what lands on L1
```

You choose the preimage, because you move first. It is derived from the wallet's own signature rather
than drawn at random, so a lost row does not lose the claim — see `PreimageProvisioning`.

The solver funds the L1 HTLC once it can see your Arkade lockup. Claiming it is driven by the advance
pass rather than by an event, because the funding lands on a chain no VTXO event reports:

```csharp
var outcome = await intents.AdvanceAsync(funded.RfqId);
// Acted: false with a Detail of "the solver has not funded the L1 HTLC yet" is an ordinary answer.
```

The claim refuses to run for less than the swap promised, and refuses to race a maturing refund leaf
— publishing the preimage for a fraction of the price, or into a window the broadcast may not win,
both cost the Arkade side as well.

If the solver never delivers, the Arkade covenant's `refundWithoutReceiver` leaf is the way back, and
the advance pass takes it once `refund_locktime` has passed.

## On-boarding

```csharp
var pending = await intents.ReceiveFromOnchainAsync(
    walletId: "my-wallet",
    amountSats: 50_000,
    rfqTransport: rfqTransport,
    covclaimdPubKey: covclaimdPubKey,   // read live from covclaimd, never hardcoded
    l1RefundAddress: BitcoinAddress.Create("bcrt1q...", Network.RegTest),
    amountSide: RfqAmountSide.From);    // pin what you send on L1

// Fund the address DERIVED here, not the one the quote names — they were compared already, and
// using ours is what makes that comparison load-bearing.
Console.WriteLine($"send {pending.FundAmountSats} sats to {pending.HtlcAddress}");
```

This returns rather than funds: the L1 funding transaction belongs to your own Bitcoin wallet, since
the sats being on-boarded are by definition not on Arkade yet.

You still choose the preimage here — the solver funds Arkade before it has been paid, so a solver
able to release the secret could collect without delivering. A copy sealed to covclaimd travels with
the request so the claim can be pushed while you are offline; the solver carries it as bytes it
cannot open. `payout_pubkey` is sent as well, so you can claim yourself: a covenant only covclaimd
can spend would make it a hard dependency of the corridor.

After `min_confirmations` the solver funds the lockup, the monitor moves the intent to `Claimable`,
and the advance pass claims it:

```csharp
await intents.ClaimOnchainReceiveAsync(pending.RfqId);
```

### The L1 refund

The only recourse on this leg — there is no Arkade covenant of yours to refund, because you never
funded one.

```csharp
var refund = await intents.RefundOnchainReceiveAsync(pending.RfqId);
```

Two things about it are worth knowing:

- **It matures against median time past (BIP-113), not wall clock**, which trails it by roughly an
  hour. A refund built against a local clock is a perfectly well-formed transaction that the network
  rejects as non-final, and the rejection says nothing about which clock was wrong. The corridor
  reads the tip's MTP and answers "not yet" until then.
- **It survives a terminal Arkade status.** The advance pass keeps proposing it even after the swap
  is written off as `Resolved` — that status means your claim window shut unused, which is exactly
  the case where the solver never learns the preimage, never claims on L1, and those sats are still
  yours to collect. Treating a status terminal for one rail as terminal for both would abandon them.

It is refused once the swap is `Fulfilled`: past that the preimage is public and the solver can take
that same HTLC, so a refund racing it is at best a wasted fee.

## Recovery

Everything above drives forward from a row the client wrote itself. When that row is gone or stale —
a restored wallet, a process down across the window, an operator asking what happened — the question
changes from "may I act yet" to "what is true", and `OnchainHtlcState` answers it from the chain.

```csharp
var status = await OnchainHtlcState.ClassifyAsync(blockchain, htlc, minConfirmations);
```

| phase | meaning |
|---|---|
| `Empty` | nothing is at the address — never funded, or funded and already spent |
| `AwaitingConfirmations` | funded, short of the count the swap was quoted at |
| `Claimable` | funded, confirmed, and there is still room before the refund leaf opens |
| `Refundable` | **the claim window is closed** — see below |

`Refundable` is the one worth being careful about. It does not mean a claim is still available: it
means the refund leaf has matured, so reaching it on a swap you expected to claim means the claim was
*missed*. The right move from there is the refund on whichever rail is yours — on an off-board that
is the Arkade covenant, not this HTLC.

Maturity is judged against the tip's **median time past**, the clock consensus applies to CLTV, which
trails wall clock by roughly an hour. Classifying on a local clock would report `Refundable` for up to
that long before a refund would actually be accepted, and — worse in the other direction — would call
a window closed while a claim could still have landed.

`Empty` is named for what an address query can actually establish. An HTLC that was never funded and
one that was funded and already spent give exactly the same answer, and `IBitcoinBlockchain` exposes
no address history to separate them — so the phase says "nothing is here" and leaves the reason to a
caller that has it. Its own row knows whether it ever funded; where a spend is suspected, turning
that inference into proof is what `ExtractPreimage` is for:

```csharp
var preimage = OnchainHtlcState.ExtractPreimage(spendingTx, paymentHash);
```

It is the L1 counterpart of `SwapPreimageReader`, which reads Arkade spends through the indexer and
cannot answer for a Bitcoin transaction. Every candidate push is checked against the payment hash
before it is believed — a 32-byte push is not evidence, one that hashes correctly is, and that is
evidence nobody can forge. A `null` answer is not proof of a refund: a spend that carried no preimage
and one that could not be read are the same silence.

For waiting rather than asking once:

```csharp
var filled = await OnchainHtlcState.AwaitFillAsync(
    blockchain, htlc, minConfirmations, within: TimeSpan.FromMinutes(30));
```

Polling, because an L1 funding raises no event this SDK subscribes to — the same reason the advance
pass proposes its onchain actions on every tick rather than on a trigger. It returns the last status
seen when the time runs out rather than throwing, so "it never arrived" stays an answer to branch on —
and it comes back as `Empty` when nothing arrived while this was watching.

## Testing

Unit coverage sits at the boundaries that can be decided without a chain:

- `OnchainSendGatesTests` / `OnchainReceiveGatesTests` — every refusal, and the ordering both ways.
- `OnchainHtlcTests` — the script, pinned against a cross-SDK fixture.
- `OnchainClaimBuilderTests` / `OnchainRefundBuilderTests` — the witness, the sighash recomputed
  independently, and for the refund the two consensus rules that fail silently: the transaction's
  `nLockTime`, and non-final input sequences without which `OP_CHECKLOCKTIMEVERIFY` is a no-op.
- `ArkadeSwapStateMachineTests` — the corridors' transitions and actions.

End-to-end coverage lives in `NArk.Tests.End2End/Arkade/ArkadeOnchainTests.cs`, under both the
`OnchainCorridors` and `ArkadeIntents` categories. It drives a real solver and the regtest Bitcoin
node:

```bash
ARKADE_LN_SOLVER_URL=http://localhost:3000 \
  dotnet test NArk.Tests.End2End --filter TestCategory=OnchainCorridors
```

The solver is not part of the regtest stack and has to be started by hand; without that variable
every test skips. They also skip when the solver answers `unsupported_pair`, since the two onchain
corridors are separately switchable in a deployment and a solver built without them is a
configuration this suite has nothing to say about.

The refund is deliberately not driven end to end: its leaf opens hours out by construction, and
winding regtest forward far enough would move the median time past under every other contract the
stack is holding. What is tested against a live, funded HTLC is the gate — that the refund declines
by the chain's own clock rather than broadcasting something the network will reject.

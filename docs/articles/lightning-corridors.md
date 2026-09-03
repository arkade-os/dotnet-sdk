# Lightning Corridors (RFQ + covenant swaps)

A second route between Arkade and Lightning, alongside the Boltz integration in
[Swaps](swaps.md). Where Boltz is a bilateral protocol with one provider, this one negotiates terms
over **RFQ** with any solver that serves the pair, and settles into a **covenant swap contract**
that neither side has to stay online for.

Two corridors:

| pair | who funds | who claims |
| --- | --- | --- |
| `arkade:BTC->lightning:BTC` | you | the solver, with the preimage paying the invoice yields |
| `lightning:BTC->arkade:BTC` | the solver | you, with a preimage you chose |

> [!NOTE]
> Both corridors negotiate against the reference solver today, and both reproduce its quoted
> `lockup_address` locally. Neither has yet been driven through a full settlement — funding
> observed, claim broadcast, the other side paid — so treat them as verified at the contract, not
> at the round trip.

## The shape of it

There is no accept message. **Funding your own derivation is acceptance.** You ask for a quote,
derive the swap contract locally from your own data plus the quote's binding fields, compare it
against the solver's `lockup_address`, and fund only on a match. After that you may go offline —
the solver observes the funding on-chain and fills; a failure refunds by covenant.

That comparison is the whole security model. A wrong or hostile solver can only ever produce an
address you decline, never one that traps your funds.

Before any of that, a reply has to be the answer to the question asked: the transports check the
quote's `rfq_id` and its `pair` before returning it. The id is what stops a stale or misrouted reply
being funded — on a shared relay every one of the solver's events arrives on the same subscription —
and the pair is compared byte for byte, because a solver that normalises case or quotes a market
other than the one asked for is otherwise undetectable from the client.

## Sending: pay a BOLT11 from an Arkade balance

```csharp
// One service over every corridor. Register it once and reach all of them through it.
var intents = new ArkadeIntentsService(
    assetSwaps, lightningSend, lightningReceive, intentStorage, vtxoStorage, TimeProvider.System);

var funded = await intents.SendToLightningAsync(
    walletId: "my-wallet",
    invoice: "lnbcrt500000n1p...",
    rfqTransport: new HttpRfqTransport(httpClient, new Uri("http://localhost:3000")));

Console.WriteLine($"locked {funded.FundedSats} sats at {funded.LockupAddress} in {funded.FundingTxid}");
```

Everything the script commits to is your own data — the payment hash from your own invoice, the
server key from your own connection, the co-signer key from this SDK's per-network pin, the refund
destination from your own wallet. From the quote it takes only the binding fields (`solver_pubkey`,
`refund_locktime`, `valid_until`, the amounts) plus `receiver_pk_script`, which is an input rather
than a promise: it names the solver's own payout, and a wrong value costs the solver a spending path
and you nothing.

The co-signer key is pinned per network rather than fetched, so neither corridor calls the emulator
to derive an address. A rotation is therefore invisible until this SDK ships the new constant —
`ArkadeIntentsOptions.EmulatorPubkeyOverride` is the escape hatch for that window, and for a
self-hosted emulator or an unpinned network. See the README's *The covenant co-signer*.

If the swap never fills, refund it once the locktime passes:

```csharp
await intents.RefundLightningSendAsync(funded.RfqId);
```

## Receiving: be paid over Lightning, take delivery on Arkade

```csharp
var pending = await intents.ReceiveFromLightningAsync(
    walletId: "my-wallet",
    amountSats: 50_000,
    rfqTransport: rfqTransport,
    covclaimdPubKey: covclaimdPubKey);   // read live from covclaimd, never hardcoded

Console.WriteLine($"have the payer settle: {pending.Invoice}");

// Once the solver funds the lockup — the monitor moves the intent to Claimable:
await intents.ClaimLightningReceiveAsync(pending.RfqId);
```

Here **you** choose the secret and send only its hash, plus a copy sealed to covclaimd that the
solver cannot open ([`ClaimPacket`](xref:NArk.ArkadeIntents.Lightning.ClaimPacket)). That asymmetry
is load-bearing: the solver funds the Arkade side before the payment it is owed has settled, so a
solver able to open the packet could settle the invoice without ever delivering.

We never talk to covclaimd ourselves — we read its public key once and seal to it, and the packet
travels to the solver as opaque bytes it forwards. What the daemon buys is the receive corridor's
answer to the problem the send corridor solves with a locktime: if you go offline between minting
the invoice and the solver funding, covclaimd holds the only other copy of your preimage and can
push the covenant's `nonInteractiveClaim` leaf on your behalf. That leaf is pinned to *your*
`receiver_pk_script`, so the daemon can complete the claim and cannot redirect it — which is what
makes handing a stranger your preimage a reasonable thing to do.

It appears on this corridor only. On a send you never hold the preimage; the payee minted it and the
solver learns it by paying, so there is nothing to seal and nothing for covclaimd to do.

The invoice it mints is checked, not trusted — `LightningReceiveGates.VerifyInvoice` refuses one for
a different payment hash (your preimage could never settle it) or a different amount than the quote
delivers. `LightningReceiveGates.AssertReceivable` then gates the deadlines before the invoice can
reach a payer: the payment deadline is the earlier of the invoice's expiry and the quote's
`valid_until`, and the solver's `refund_locktime` must leave at least 30 minutes of claim window
after it — a payment into a window too short to claim in parks the payer's money in a held HTLC
until it lapses.

Claiming is not optional tidy-up. The preimage becomes public in the claim witness, and that is what
lets the held invoice settle — so an unclaimed swap is one where the solver reclaims its lockup and
the payer's money was never earned. The claim refuses once `refund_locktime` has passed rather
than race that reclaim for the same output, and it refuses to publish the preimage for less than
the quoted amount: the lockup address is public, so the gate is on what the lockup actually holds,
with every live output claimed together.

The preimage is persisted on the intent before the invoice is handed out, because there is no
recovering it afterwards: you chose it, and the only other copy is sealed to a key you do not hold.
The covclaimd packet is a fallback claimer, not a backup you can read.

## Reaching a solver

Two transports, same payloads.

`HttpRfqTransport` posts to a solver that happens to expose a port — convenient locally, and what
the reference solver offers.

`NostrRfqTransport` is the one the protocol specifies: NIP-01 over a relay, both parties dialling
out, addressed by x-only pubkey. No URLs appear in the protocol at all, which is what lets a solver
run with no inbound port and no DNS name — and it is the only way to use a registry card, since a
card carries a discovery pubkey and relays rather than an address.

```csharp
using var transport = NostrRfqTransport.ForCard(card);
```

**Build it from the card, and it dials the whole relay set.** A card advertises a list, and every
entry is dialled at once with the first valid reply winning. That is the point of a set rather than
an optimisation: a rendezvous is a place both parties happen to be, and neither side controls which
entry the other is connected to at this moment — so dialling one and waiting is a coin flip dressed
up as a protocol. The same signed event goes to all of them, which a solver connected to several
sees as duplicates of one request, idempotent by negotiation id.

Non-`wss://` entries are dropped rather than dialled. The registry schema admits only `wss://`, so a
plaintext entry is either a malformed card or a downgrade someone wants accepted — and while this
traffic is sealed to the solver's key, it is not sealed to the relay's, so who carries it still
matters. Duplicates collapse to one connection.

Each negotiation uses a fresh identity key by default, so separate swaps are unlinkable to the relay
operator and a stale archive can never be replayed at us. Pass a stable key when talking to one
solver repeatedly — the ECDH it saves is the dominant per-message cost.

### Three silences, not one

A negotiation that produces no quote has three quite different causes, and a transport that reports
them all as a timeout blames the counterparty for two of them:

| | meaning | who to look at |
|---|---|---|
| `NostrRelayException` | a relay refused the event, tore the subscription down, or nobody answered in time | the solver, or that relay |
| `RelayUnavailableException` | no relay was ever listening | our own side of the wire |
| `TransportClosedException` | the transport was disposed mid-negotiation | our own caller |

The middle one carries `Reasons`, one entry per relay, so an operator can see which member of the
set was actually broken. Without that distinction a client waits out the full timeout and then files
a bug against a solver that was never asked — which is how the reference deployment's own outage
stayed invisible for days.

## The covenant contract

Both corridors build the same eight-leaf contract,
[`VHTLCv2Contract`](xref:NArk.Arkade.Contracts.VHTLCv2Contract) — six leaves of the reference VHTLC
plus two whose co-signer is an emulator key tweaked by a covenant pinning where the spend may pay.

Eight is this corridor's shape, not the class's only one. Both covenant leaves are optional, and the
covenant can bind an [Arkade asset](xref:NArk.Arkade.Contracts.VHTLCv2Asset) or a
[quoted amount](xref:NArk.Arkade.Contracts.VHTLCv2StrictClaim). Each is a different merkle root and
therefore a different address, which is why the option set is as much a part of the agreement as the
keys are — a solver that turns one on is quoting an address this corridor's shape will not
reproduce.

Roles are **positional**, not fixed to a party. On the send corridor you are `sender` and the solver
is `receiver`; on the receive corridor they swap.

The contract has four refund leaves on a send swap, but only one of them is a recourse you can
actually reach on your own, and the difference is worth knowing before the moment you need it:

| leaf | who signs | when | can you start it? |
| --- | --- | --- | --- |
| `refund` | you + solver + server | immediately | no |
| `unilateralRefund` | you + solver | after a CSV delay | no |
| `refundWithoutReceiver` | you + server | after `refund_locktime` | **yes** — this is `RefundLightningSendAsync` |
| `unilateralRefundWithoutReceiver` | you alone | after the longest delay | only by exiting to the chain |

The first two need the solver's signature, and **the RFQ protocol has no message asking for one**.
A solver may push a refund of its own accord, but that is an operator action on its side, not
something a client can request.
So a leaf that reads as faster on paper is not a faster path for you; it is a path that opens only
if the solver independently decides to take it.

The last leaf needs nobody, which sounds like the real backstop and is not. Reaching it means
unrolling the VTXO to the chain, and an eight-leaf covenant arrives there carrying enough script
data that the exit costs more than it recovers. That is a deliberate property of the construction,
not a gap in this SDK: the contract is priced so that waiting for `refund_locktime` and taking
`refundWithoutReceiver` is the sane move, and the Arkade server declining to co-sign is the only
scenario the arithmetic assumes away.

The three CSV delays are **not carried on the wire**. Both sides derive them from the Arkade
operator's own `unilateralExitDelay`, rounded up to a whole BIP68 unit: the claim and the two-party
refund sit level at that base (neither of those leaves is spendable alone, so separating them buys
nothing), and only the solo refund gets real headroom on top — 8 BIP68 units (4096 seconds), sized
for what reaching the claim costs with the server gone. A delay the solver could dictate is a delay
it could stretch.

The key behind leaves 1–4 is the one that owns your refund address, so it is on your wallet's own
derivation chain and survives a restart with no extra storage.

## Keeping up with the solver

The contract is an agreement about bytes, and it is not versioned on the wire: if your derivation
and the solver's ever disagree, the first symptom is funds at an address nobody can spend. The
defence is a set of golden vectors generated from the counterparty's own implementation.

Regenerate them whenever the solver moves to a newer ts-sdk pin:

```bash
node NArk.Tests/ArkadeIntents/Fixtures/generate-covenant-vectors.mjs \
  <node-project-with-arkade-sdk> > NArk.Tests/ArkadeIntents/Fixtures/covenant_swap.json
dotnet test NArk.Tests --filter VHTLCv2ContractTests
```

If the vectors and this SDK disagree, **this SDK is what is wrong** — they come from the side that
will or will not be able to spend.

## In the sample wallet

`samples/NArk.Wallet` exercises both corridors from a browser, and is the shortest way to see the
shape of an integration. Send pays a BOLT11 or an LNURL address; Receive mints an invoice; the Swap
page lists Lightning swaps alongside asset ones and carries the claim and refund buttons.

It replaces what used to be Boltz submarine and reverse swaps in that sample. The Boltz chain swaps
stay where they are — there is no intent corridor for on-chain BTC yet, so removing them would have
cost the sample a feature rather than moved it.

Everything corridor-specific lives in `Services/ArkadeLightningService.cs`, which owns three
decisions worth copying:

**The solver comes from the registry, not from configuration.** Nothing about a counterparty is
baked into the build: the sample asks the public index which solvers advertise a Lightning corridor
on its network and takes one. The index carries a solver's key but not its relays, so the relay is
a configured default — an asymmetry of the current index format rather than a choice, and the one
place a corridor found through discovery still cannot dial itself.

**The transport is built per negotiation and disposed with it.** The RFQ kinds are ephemeral, so a
relay stores nothing and there is no backlog a longer-lived connection could catch up from. Holding
a socket open between swaps buys nothing and, in a browser tab that sleeps, costs something. No
identity key is passed either, so each negotiation signs with a fresh one and the relay operator
cannot link a wallet's swaps to each other.

**covclaimd's key is read live, every time.** The daemon generates it at startup, so a restart
invalidates any copy — and a preimage sealed to a key nobody holds fails silently: the swap works,
and only its offline claim path quietly does not exist.

The status labels are worth a look too. On these corridors `Resolved` means the swap ended without
a proven preimage — a spend that revealed none (a refund, not a payment), or a claim window that
lapsed — so the sample says "Refunded — the payment did not happen" rather than anything that reads
like success. `Fulfilled` is reserved for a spend whose witness carries a preimage hashing to the
swap's payment hash, which is provable rather than inferred; the monitor checks it on every spend,
and reconciliation re-checks it, so a `Resolved` recorded on a transient indexer miss is upgraded
once the proof is readable. A wallet that collapses those two into "done" reports a failed payment
as a completed one.

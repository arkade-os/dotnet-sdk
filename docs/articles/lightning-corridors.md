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

> [!IMPORTANT]
> The receive corridor is complete here but has **no counterparty yet**: the reference solver serves
> it internally and does not route it on any transport, so nothing can negotiate against it until
> that lands.

## The shape of it

There is no accept message. **Funding your own derivation is acceptance.** You ask for a quote,
derive the swap contract locally from your own data plus the quote's binding fields, compare it
against the solver's `lockup_address`, and fund only on a match. After that you may go offline —
the solver observes the funding on-chain and fills; a failure refunds by covenant.

That comparison is the whole security model. A wrong or hostile solver can only ever produce an
address you decline, never one that traps your funds.

## Sending: pay a BOLT11 from an Arkade balance

```csharp
var client = new LightningSwapClient(
    transport, emulator, contractService, spendingService,
    intentStorage, contractStorage, vtxoStorage, walletProvider);

var funded = await client.SendToLightningAsync(
    walletId: "my-wallet",
    invoice: "lnbcrt500000n1p...",
    rfqTransport: new HttpRfqTransport(httpClient, new Uri("http://localhost:3000")));

Console.WriteLine($"locked {funded.FundedSats} sats at {funded.LockupAddress} in {funded.FundingTxid}");
```

Everything the script commits to is your own data — the payment hash from your own invoice, the
server key from your own connection, the emulator key from your own fetch, the refund destination
from your own wallet. From the quote it takes only the binding fields (`solver_pubkey`,
`refund_locktime`, `valid_until`, the amounts) plus `receiver_pk_script`, which is an input rather
than a promise: it names the solver's own payout, and a wrong value costs the solver a spending path
and you nothing.

If the swap never fills, refund it once the locktime passes:

```csharp
await client.RefundSwap(funded.RfqId);
```

## Receiving: be paid over Lightning, take delivery on Arkade

```csharp
var client = new LightningReceiveClient(
    transport, emulator, contractService, spendingService,
    intentStorage, contractStorage, vtxoStorage);

var pending = await client.ReceiveFromLightningAsync(
    walletId: "my-wallet",
    amountSats: 50_000,
    rfqTransport: rfqTransport,
    covclaimdPubKey: covclaimdPubKey);   // read live from covclaimd, never hardcoded

Console.WriteLine($"have the payer settle: {pending.Invoice}");

// Once the solver funds the lockup — the monitor moves the intent to Claimable:
await client.ClaimAsync(pending.RfqId);
```

Here **you** choose the secret and send only its hash, plus a copy sealed to covclaimd that the
solver cannot open ([`ClaimPacket`](xref:NArk.ArkadeIntents.Lightning.ClaimPacket)). That asymmetry
is load-bearing: the solver funds the Arkade side before the payment it is owed has settled, so a
solver able to open the packet could settle the invoice without ever delivering.

The invoice it mints is checked, not trusted — `LightningReceiveGates.VerifyInvoice` refuses one for
a different payment hash (your preimage could never settle it) or a different amount than the quote
delivers.

Claiming is not optional tidy-up. The preimage becomes public in the claim witness, and that is what
lets the held invoice settle — so an unclaimed swap is one where the solver reclaims its lockup and
the payer's money was never earned. `ClaimAsync` refuses once `refund_locktime` has passed rather
than race that reclaim for the same output.

The preimage is persisted on the intent before the invoice is handed out, because there is no
recovering it afterwards: you chose it, and the only other copy is sealed to a key you do not hold.
The covclaimd packet is a fallback claimer, not a backup you can read.

## The covenant contract

Both corridors build the same eight-leaf contract,
[`VHTLCv2Contract`](xref:NArk.Arkade.Contracts.VHTLCv2Contract) — six leaves of the reference VHTLC
plus two whose co-signer is an emulator key tweaked by a covenant pinning where the spend may pay.

Roles are **positional**, not fixed to a party. On the send corridor you are `sender` and the solver
is `receiver`; on the receive corridor they swap.

Your ladder of recourse on a send swap, fastest first:

1. `refund` — you + solver + server, immediately
2. `unilateralRefund` — you + solver after a CSV delay, no server
3. `refundWithoutReceiver` — you + server after `refund_locktime`, no solver ← what `RefundSwap` uses
4. `unilateralRefundWithoutReceiver` — you alone after the longest delay, needing nobody

The three CSV delays are **not carried on the wire**. Both sides derive them from the Arkade
operator's own `unilateralExitDelay`, rounded up to a whole BIP68 unit and then one unit per rung —
a delay the solver could dictate is a delay it could stretch.

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

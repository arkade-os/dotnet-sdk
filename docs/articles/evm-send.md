# EVM send and composed receives

`ComposedSwapClient` builds a merchant payment route from independent solver RFQs. The client, not a
solver, composes the legs. It supports direct Arkade funding and Lightning or onchain BTC receives
that settle into an ERC20 destination.

## Create one route per payment method

Create a fresh route for every payment method and every invoice renewal. Each route gets its own
random preimage `P` and payment hash `H = SHA256(P)`; do not share a hash between alternatives.

For a Lightning or onchain route the SDK performs these ordered steps:

1. It creates the exact-input Arkade-to-EVM RFQ first. This persists outgoing lock `L`, its `P`, and
   `H` before any ingress quote exists.
2. It creates a second, independent exact-output ingress RFQ with the same `H`. Its non-interactive
   claim sends ingress lock `M` to the exact script for `L`.
3. It returns the customer-facing Lightning invoice or L1 address. The ingress `from - to` spread is
   the payment-method fee; `to` is exactly the Arkade amount required by `L`.

```csharp
var route = await composer.CreateLightningAsync(
    walletId, amountSats, merchantEvmAddress, evmPolicy,
    evmSolverTransport, lightningSolverTransport, covclaimdPubkey,
    cancellationToken: cancellationToken);

ShowInvoice(route.Ingress.Invoice, route.CheckoutExpiresAt);
```

`CreateOnchainAsync` follows the same sequence and additionally takes the customer's L1 refund
address. `CreateArkadeAsync` creates only `L`, for direct Arkade funding. The quote validators bind
the exact satoshi amount, full-width ERC20 atomic amount, hash, chain, token, contract, locktimes,
and the quoted covenant script before a route can be shown.

## Watch-only settlement

A BTCPay-style server can use a watch-only Arkade wallet: it does not need the merchant's Arkade
spending key to move a funded `M`. Configure the emulator service and pass the live covclaimd public
key at quote time. Once the ingress is claimable, the executor uses the non-interactive emulator path
to claim `M` into `L`. Schedule `ComposedSwapExecutionClient.AdvanceAsync` for each open route; the
generic `ArkadeIntentsService.AdvanceAsync` and `AdvanceAllAsync` deliberately leave linked receive
claims alone so their signer-backed receive loop cannot race the composed executor's spend lock.

```csharp
var progress = await executor.AdvanceAsync(
    route.Outgoing.RfqId, route.Ingress.RfqId, cancellationToken);

if (progress.EvmClaimTxid is not null)
    MarkInvoiceSettled(progress.EvmClaimTxid, progress.DeliveredAmount);
```

The executor does not treat an ingress payment or the `M`-to-`L` claim as settlement. Claiming `M`
reveals `P` before the EVM solver necessarily locks funds, so deployment relies on the configured
outgoing solver's EVM state machine. The only final success is a verified `claimFor` receipt with the
expected `Claim` event, exact ERC20 `Transfer`, and consumed EVM swap. If `L` reaches its Arkade
refund deadline instead, the executor uses the non-interactive refund path; do not promise unattended
recovery unless the configured emulator path has been tested in the target deployment.
An external covclaimd claim is compatible with the same loop: synchronization observes `M` spent and
`L` funded, after which the composed executor continues from persisted state without submitting M
again.

## Durable EVM claim and recovery

The EVM sender must implement `IEvmDurableTransactionSender`. It computes the deterministic signed
transaction and persists both its hash and exact signed bytes through the executor callback before
broadcasting. On a restart, `AdvanceAsync` first checks whether that exact transaction was mined, even
when either deadline has since passed. Only a receipt timeout permits replay: the executor rechecks
the live EVM claim window and Arkade refund margin, confirms the lock remains active, and then
rebroadcasts the same bytes; it never signs a replacement claim. If either window is no longer safe,
the submitted/prepared journal remains in place, no transaction or Arkade refund is broadcast, and
the route requires manual intervention. Recovery of a prepared EVM claim runs before ordinary Arkade
status and refund handling, preventing a delivered EVM claim from being followed by an Arkade refund.
The host should serialize a route across processes because intent storage does not provide a
compare-and-swap primitive.

A mined failed claim receipt is not settlement and is not automatically followed by an Arkade
refund: its calldata may already have revealed `P`. Keep the route for operator intervention rather
than clearing the prepared artifact or signing a replacement transaction automatically.

`P` is intentionally stored in SDK intent metadata so the route can recover after a restart. Treat
the intent store as secret-bearing storage: protect it at rest, restrict access, and never return or
log its metadata. The prepared EVM transaction also contains `P` in its claim calldata and requires
the same protection. Likewise, load the EVM gas-payer key from a server secret provider into a
short-lived buffer; do not put it in JSON options or logs. The sender validates the derived gas-payer
address, applies configured fee and gas caps, and discards RPC error bodies because they can contain
`P`.

Immediately before signing, the executor rechecks the configured minimum EVM claim window and the
margin between the latest plausible EVM expiry and the Arkade refund deadline. A pending outgoing leg
is eligible only when synchronized `IVtxoStorage` contains the exact quoted BTC amount at `L`, and all
of those VTXOs are currently off-chain spendable according to `IBitcoinBlockchain` chain time.
Unrolled, unconfirmed on-chain, expired, asset-bearing, spent, and swept funding cannot authorize the
EVM claim.

The gateway sample's `ComposedEvmSettlementExample` registers the composer and durable executor next
to `EvmServerChainExample`. It requires the normal Arkade intent clients, intent/contract storage,
network transport, synchronized `IVtxoStorage`, `IBitcoinBlockchain`, `EvmJsonRpcClient`, and
`EvmLocalTransactionSender` registrations.

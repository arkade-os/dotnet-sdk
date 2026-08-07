# Batch Expiry Validation

Every batch an Arkade server opens carries a **batch expiry**. It is the one value in the batch
protocol a client cannot verify from anything else, so the SDK bounds it explicitly before committing
to it.

## Why it matters

The batch output is a taproot output whose internal key is an N-of-N MuSig2 aggregate over every
cosigner in the batch — including your wallet. Nobody can spend it through the key path alone. Its
tap tree holds a single **sweep leaf**:

```
<batchExpiry> OP_CHECKSEQUENCEVERIFY OP_DROP <forfeitPubKey> OP_CHECKSIG
```

That leaf is the operator's only unilateral path out of the shared output. Once the expiry elapses,
the operator can sweep whatever has not been unrolled — which is the mechanism that lets an operator
reclaim funds from users who went offline and never exited.

The expiry therefore sets your deadline: you must unilaterally exit before it elapses, or the
operator can take the funds.

## Why tree validation cannot catch a bad expiry

Before signing, the SDK validates the whole VTXO tree: every node's output must equal the cosigner
aggregate tweaked with the sweep tap tree root (`TreeValidator.ValidateVtxoTxGraph`). That check is
real, but the root it compares against is derived by the client *from the expiry the operator just
sent*. It proves the tree is consistent with whatever value was supplied, not that the value is safe.

So an operator that declares a one-block expiry produces a tree that validates perfectly. The client
signs it, forfeits its inputs, and one block after the commitment transaction confirms, the operator
sweeps the batch output — with no unilateral exit fast enough to win that race.

Because the expiry only ever reaches the chain as part of a leaf *hash*, no amount of downstream
inspection recovers it. Bounding it up front is the only defence.

## What the SDK enforces

`BatchExpiryPolicy` runs inside `BatchSession.InitializeAsync`, which `BatchManagementService` calls
*before* `ConfirmRegistration`. A rejected batch is never confirmed, no signing state is built, and
the intent is failed with `InvalidBatchExpiryException`.

| | Mainnet / testnet / signet | Regtest |
| --- | --- | --- |
| Block-typed expiry | rejected | allowed, minimum 10 blocks |
| Seconds-typed expiry | minimum 24 hours | minimum 512 seconds |

Block-typed expiries are rejected off regtest for two reasons: arkd itself only permits a block-typed
VTXO tree expiry on regtest, and a bound expressed in blocks says nothing about wall-clock time,
which is what actually determines whether you can exit in time.

Note that a server advertising `mutinynet` resolves to `Network.TestNet`, so it gets the strict
policy.

## BIP-68 granularity

Relative timelocks encode seconds in units of 512, so a declared expiry is rounded **down** to a
multiple of 512 when encoded. The SDK:

- compares the floor against the *encoded* value, since that is what the leaf actually commits to;
- rounds the floor down to the same granularity, so a 24-hour floor accepts `86016` (168 × 512)
  rather than rejecting the closest encodable value below 24 hours for being 384 seconds short;
- accepts a value that is not a multiple of 512 but logs a warning naming both the declared and the
  encoded number.

The warning is deliberately not an error. Truncation is not a theft vector — the encoded value is
what goes into the leaf hash, so a server that rounds differently simply fails tree validation. The
warning exists to make that otherwise opaque rejection legible.

## Configuration

The defaults are on by default and need no setup. Override them only for a server whose configuration
they reject:

```csharp
builder.AddArk().ConfigureBatchExpiry(options =>
{
    options.MinimumExpiry = TimeSpan.FromHours(6);
});
```

or, without the host builder:

```csharp
services.Configure<BatchExpiryOptions>(options =>
{
    options.MinimumExpiry = TimeSpan.FromHours(6);
});
```

`BatchExpiryOptions` exposes three optional properties; a `null` property keeps the network default:

- `MinimumExpiry` — floor for seconds-typed expiries.
- `MinimumExpiryBlocks` — floor for block-typed expiries.
- `AllowBlockTypedExpiry` — opts a non-regtest network into block-typed expiries. Only set this for a
  server you know is configured that way.

The floors can be lowered but not disabled: a floor of zero or less throws `ArgumentOutOfRangeException`
when the policy is built, rather than silently turning the check off.

## Limitations

- The Arkade server does not currently advertise its configured VTXO tree expiry
  (`GetInfoResponse` has no such field), so the SDK cannot additionally assert that the per-batch
  value matches the server's own advertised policy. If that field returns, an equality check belongs
  here too.
- The same bounding is not yet applied to the checkpoint unroll script or to the unilateral and
  boarding exit delays advertised in server info.

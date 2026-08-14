# Batch Expiry Validation

Every batch an Arkade server opens carries a **batch expiry**, and it is the one value in the batch
protocol a client cannot verify from anything else.

## Why it can't be verified

The batch output is a taproot output whose internal key is an N-of-N MuSig2 aggregate over every
cosigner in the batch, your wallet included — so the key path is unspendable without you. Its tap tree
holds a single **sweep leaf**:

```
<batchExpiry> OP_CHECKSEQUENCEVERIFY OP_DROP <forfeitPubKey> OP_CHECKSIG
```

That leaf is the operator's only unilateral path out of the shared output, and the mechanism by which
it reclaims funds from users who went offline. The expiry is therefore your deadline: exit before it
elapses, or the operator can take the funds.

Before signing, the SDK validates the whole VTXO tree — every node's output must equal the cosigner
aggregate tweaked with the sweep tap tree root (`TreeValidator.ValidateVtxoTxGraph`). But the root it
compares against is derived by the client *from the expiry the operator just sent*, so the check
proves the tree is self-consistent, not that the expiry is safe.

An operator declaring a one-block expiry thus produces a tree that validates perfectly. The client
signs it, forfeits its inputs, and one block after the commitment transaction confirms, the operator
sweeps the batch output — no unilateral exit wins that race. Since the expiry only reaches the chain
inside a leaf *hash*, no downstream inspection recovers it. Bounding it up front is the only defence.

## What the SDK enforces

`BatchExpiryPolicy` runs in `BatchSession.InitializeAsync`, which `BatchManagementService` calls
*before* `ConfirmRegistration`. A rejected batch is never confirmed, no signing state is built, and
the intent fails with `InvalidBatchExpiryException`.

| | Mainnet / testnet / signet | Regtest |
| --- | --- | --- |
| Block-typed expiry | rejected | allowed, minimum 10 blocks |
| Seconds-typed expiry | minimum 24 hours | minimum 512 seconds |

Block-typed expiries are rejected off regtest for two reasons: arkd only permits a block-typed VTXO
tree expiry there, and a bound in blocks says nothing about wall-clock time, which is what determines
whether you can exit in time. Note that a server advertising `mutinynet` resolves to
`Network.TestNet`, so it gets the strict policy.

## BIP-68 granularity

Relative timelocks encode seconds in units of 512, so a declared expiry is rounded **down** when
encoded. The SDK compares floors against the encoded value, since that is what the leaf commits to,
and rounds the floor to the same granularity — a 24-hour floor accepts `86016` (168 × 512) rather than
rejecting the closest encodable value below 24 hours for being 384 seconds short.

A value that is not a multiple of 512 is accepted with a warning naming both numbers. Truncation is
not a theft vector — a server that rounds differently simply fails tree validation — so the warning
exists to make that otherwise opaque rejection legible.

## Configuration

Validation needs no setup. Override it only for a server whose expiry the defaults reject:

```csharp
builder.AddArk().ConfigureBatchExpiry(options =>
{
    options.MinimumExpiry = TimeSpan.FromHours(6);
});
```

Without the host builder, configure `BatchExpiryOptions` directly. Each property is optional; `null`
keeps the network default:

- `MinimumExpiry` — floor for seconds-typed expiries.
- `MinimumExpiryBlocks` — floor for block-typed expiries.
- `AllowBlockTypedExpiry` — opts a non-regtest network into block-typed expiries. Only set this for a
  server you know is configured that way.

Floors can be lowered but not disabled: zero or less fails at startup rather than silently turning the
check off.

## Limitations

- The Arkade server no longer advertises its configured VTXO tree expiry, so the SDK cannot also
  assert that a batch's value matches the server's own policy. If that field returns to
  `GetInfoResponse`, an equality check belongs here.
- The same bounding is not yet applied to the checkpoint unroll script, or to the unilateral and
  boarding exit delays in server info.

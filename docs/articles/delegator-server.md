# Delegator Server

Arkade VTXOs expire if they are not periodically refreshed (re-enrolled into a new batch). A
*delegator* is a server that refreshes a client's VTXOs on the client's behalf, so the client can go
offline. NArk consumes a delegator (see [Delegation in the README](../../README.md#delegation)) and,
with the `NArk.Delegator` library, can also **be** one — the server side of the
`fulmine.v1.DelegatorService`.

## The delegate contract

A client that wants delegation funds its VTXOs at an `ArkDelegateContract` — a Taproot contract with
three tapscript leaves:

- **DelegatePath** — `user + delegate + server` multisig. The forfeit transaction that re-enrolls the
  VTXO spends this leaf; the client pre-signs the user position, the delegator co-signs the delegate
  position, and the operator completes it.
- **CollaborativePath** — `user + server`. The intent proof (a BIP322 message) is built over this leaf.
- **ExitPath** — `user` only, after a CSV delay. The owner's unilateral escape hatch.

The `delegate` key is the delegator's own key, published by `GetDelegatorInfo`. Because every leaf is
defined purely by `{server, user, delegate, exit-delay}` and the Taproot tree uses a fixed unspendable
internal key, the delegator can deterministically **reconstruct** a client's contract from a received
forfeit (identifying the user key as the leaf key that is neither its own nor the operator's) and
verify the reconstruction against the VTXO's scriptPubKey.

## Hosting

`AddNArkDelegator(...)` + `MapNArkDelegator()` mount the service (gRPC + REST via JSON transcoding) in
any ASP.NET Core host. `DelegatorOptions` configures the delegator wallet, its signing descriptor, the
flat service fee, and the Arkade fee address. The host must also provide the standard Arkade services
(wallet, transport, intent/contract/VTXO storage).

## Intake (`Delegate`)

For each delegation the service:

1. Parses the intent proof + each forfeit PSBT.
2. Reconstructs the delegate contract from the forfeit's delegate leaf and verifies its scriptPubKey
   matches the VTXO being spent (rejecting anything that does not carry this delegator's key).
3. **Co-signs the forfeit's delegate path** with the delegator's signer (`ALL|ANYONECANPAY`, which keeps
   the signature valid when the operator's connector input is grafted in at batch time).
4. Applies `reject_replace` / supersede logic for VTXOs already covered by an active delegation.
5. When a fee is configured, verifies the intent pays it to the delegator's address.
6. Persists the delegation as a `WaitingToSubmit` intent with `ValidFrom = expiry − threshold`, plus the
   co-signed forfeits — reusing the existing intent storage and pipeline.

## Forfeit protocol

The forfeit is a TRUC (version 3) transaction spending the VTXO to the operator's forfeit address
(`amount + Dust`) plus a P2A anchor, with the connector input added by the delegator at batch time. The
client signs `ANYONECANPAY|ALL`; the delegator's co-signature is added at intake. The operator's
signature is only required at fraud-redemption time, so the user + delegate signatures suffice when the
forfeit is submitted in a batch.

## Status

The intake flow above is implemented and tested end-to-end against regtest arkd. The automated
**batch refresh** — registering the held intent at `ValidFrom` and joining the batch to submit the
stored forfeit (with the connector appended) — is the tracked next step.

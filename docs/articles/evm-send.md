# EVM send

This slice prepares and verifies an Arkade BTC to ERC20 send. It deliberately does not provide a
long-lived orchestration or persistence service: ERC20 atomic quantities and timeout heights are
`BigInteger` values and must not be stored in `Money` or `ArkadeSwapIntent.WantAmount`.

## Prepare before funding

The client creates the preimage and SHA-256 payment hash, then asks the EVM send solver for an
exact-input quote. The current wire request encodes Arkade satoshis as a JSON number with
`amount_side: "from"`; the quote encodes the ERC20 `to_amount` as a canonical decimal string.

```csharp
var request = EvmSendProfile.Request(
    amountSats: 50_000,
    paymentHash: paymentHash,
    evmClaimAddress: walletAddress,
    refundAddress: arkadeRefundAddress,
    clientRefundPubkey: Convert.ToHexString(refundDescriptor.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
    tokenAddress: policy.TokenAddress);

var quote = await rfq.RequestEvmSendQuoteAsync(request, cancellationToken);
var swap = EvmSendQuoteValidator.Validate(request, quote, policy, now, evmTip);
var lockup = EvmArkadeLockupValidator.Validate(
    request, quote, policy, serverInfo, refundDescriptor, refundPkScript, emulatorPubkey);
```

The first gate binds the RFQ envelope, exact satoshi input, full-width token output, payment hash,
configured chain/token/contract, confirmation depth, block age and deadlines. Fractional block
cadences use exact integer-rational comparisons, so a sub-second chain does not lose safety through
rounding. The second gate derives both VHTLCv2 shapes from the live operator key, current unilateral
delays, emulator key, solver key and local refund script. Funding is allowed only when the derived
script commitment matches the quoted address.

The default policy also requires the nine-leaf `nonInteractiveRefundWithoutReceiver` capability.
This proves that the quoted script contains the server-plus-emulator path after its locktime; it does
not make the SDK a watch-only refund executor. Pushing that refund through the emulator remains a
separate capability and must be proven before an application promises unattended recovery.

## Prove and claim

Provide narrow adapters for the application's EVM RPC and signer. The SDK owns ABI encoding and
verification but never handles a private EVM key.

```csharp
var chain = new EvmSwapChainClient(rpc, signer, policy);
var proof = await chain.ProveLockAsync(swap, cancellationToken);
var delivered = await chain.ClaimForAsync(swap, preimage, cancellationToken);
```

`ProveLockAsync` verifies the configured chain and calls `swaps(key)` both at the current tip and at
the block proving the configured depth and age. `ClaimForAsync` re-reads the timeout and live swap
state immediately before signing, submits permissionless `claimFor`, and then requires all of:

- a successful receipt for the submitted transaction;
- exactly one `Claim(bytes32,bytes32)` log from the configured ERC20Swap contract;
- a 32-byte event preimage whose SHA-256 equals the payment hash;
- exactly one configured-token `Transfer` from the swap contract to the claim address for the
  quoted atomic amount; and
- `swaps(key) == false` after the claim.

The solver's public EVM quote-status route currently returns 404. A composed application should
store its own typed operation record containing `P`, `H`, the Arkade lockup `L`, the six swap values
and lifecycle evidence, then derive state from Arkade and EVM truth. Ingress may be direct Arkade,
Lightning-to-Arkade or onchain-to-Arkade; it does not change the EVM send contract.

// Golden vectors for the onchain corridor's L1 HTLC, generated from the SOLVER's own
// derivation — `onchainHtlcScript` out of `@arkade-os/swap`. Our C# reconstruction must agree
// byte for byte: a single byte of drift yields an address the counterparty cannot spend.
//
//   node generate-onchain-htlc-vectors.mjs <path-to-@arkade-os/swap> > onchain_htlc.json
//
// Inputs are fixed, not random: these are a cross-implementation agreement check, and a changing
// input would hide a real derivation drift behind a noisy diff.
import { onchainHtlcScript, paymentHashOf, LOCKTIME_THRESHOLD } from
  (process.argv[2] ?? '@arkade-os/swap')
import { hex } from '@scure/base'

const toHex = (b) => Buffer.from(b).toString('hex')

// Fixed inputs: a cross-implementation agreement check, so a changing input would
// hide real derivation drift behind a noisy diff.
const preimage = hex.decode('a3'.repeat(32))
const paymentHash = paymentHashOf(preimage)
const claimKey = hex.decode('1b84c5567b126440995d3ed5aaba0565d71e1834604819ff9c17f5e9d5dd078f')
const refundKey = hex.decode('7c2a5ee7f0d4f5f61b0b6b1d4c9a83a0e2f5c6d7889a0b1c2d3e4f5061728394')
const refundLocktime = 1800000000

const out = {
  _comment: 'GENERATED — do not hand-edit. Produced by gen-onchain.mjs from @arkade-os/swap onchainHtlcScript.',
  _lockTimeThreshold: LOCKTIME_THRESHOLD,
  inputs: {
    preimage: toHex(preimage),
    paymentHash,
    claimKey: toHex(claimKey),
    refundKey: toHex(refundKey),
    refundLocktime,
  },
  networks: {},
}

for (const network of ['bitcoin', 'testnet', 'regtest']) {
  const h = onchainHtlcScript({ paymentHash, claimKey, refundKey, refundLocktime }, network)
  out.networks[network] = {
    address: h.address,
    pkScript: toHex(h.pkScript),
    claimLeaf: toHex(h.leaves.claim),
    refundLeaf: toHex(h.leaves.refund),
    claimControlBlock: toHex(h.controlBlocks.claim),
    refundControlBlock: toHex(h.controlBlocks.refund),
  }
}
console.log(JSON.stringify(out, null, 2))

// Golden vectors for the Lightning corridors' covenant swap script, generated from the
// SOLVER's own derivation — `VHTLC.ScriptV2` out of `@arkade-os/sdk`, the exact class
// a solver wraps to produce the `lockup_address`
// it quotes. Our C# reconstruction must agree byte for byte: a single byte of drift yields an
// address the counterparty cannot spend, with the deposit stuck until the refund locktime.
//
// We do not pin the SDK ourselves, so this script exists to make a
// contract rebuild cheap. Re-run it whenever the solver moves to a newer ts-sdk pin:
//
//   node NArk.Tests/ArkadeIntents/Fixtures/generate-covenant-vectors.mjs \
//     <node-project> > NArk.Tests/ArkadeIntents/Fixtures/covenant_swap.json
//
// It resolves `@arkade-os/sdk` out of that project's node_modules, so it always reflects the
// SDK pin it was installed with (install its dependencies first).
//
// Inputs are fixed, not random: these vectors are a cross-implementation agreement check, and
// a changing input would hide a real derivation drift behind a noisy diff.

import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const modulesPath = process.argv[2]
if (!modulesPath) {
  console.error('usage: generate-covenant-vectors.mjs <node-project-with-the-required-npm-deps>')
  process.exit(2)
}

const require = createRequire(pathToFileURL(resolve(modulesPath, 'package.json')))
const sdkEntry = require.resolve('@arkade-os/sdk')
const { VHTLC } = await import(pathToFileURL(sdkEntry).href)

// `@arkade-os/sdk` does not export ./package.json, so read it off the resolved entry's package
// root instead. Recording the version is what makes a regenerated diff self-explaining.
//
// Walk up from the entry rather than slicing on `/@arkade-os/sdk/`: node resolves through symlinks
// by default, so a workspace checkout linked into node_modules reports its real path
// (`.../ts-sdk/packages/ts-sdk/dist/index.js`), which carries no such segment. Regenerating against
// a local ts-sdk is exactly what this script is for whenever the change under test has not been
// published yet.
const sdkVersion = (() => {
  for (let dir = dirname(sdkEntry); ; dir = dirname(dir)) {
    try {
      const pkg = JSON.parse(readFileSync(resolve(dir, 'package.json'), 'utf8'))
      if (pkg.name === '@arkade-os/sdk') return pkg.version
    } catch { /* keep walking */ }
    const parent = dirname(dir)
    if (parent === dir) throw new Error(`no @arkade-os/sdk package.json above ${sdkEntry}`)
  }
})()

const hex = (s) => Uint8Array.from(Buffer.from(s, 'hex'))
const toHex = (b) => Buffer.from(b).toString('hex')

// Reused verbatim from the previous three-leaf fixture so the two generations stay comparable.
const SERVER = hex('531fe6068134503d2723133227c867ac8fa6c83c537e9a44c3c5bdbdcb1fe337')
const EMULATOR = hex('56b328b30c8bf5839e24058747879408bdb36241dc9c2e7c619faa12b2920967')
const PREIMAGE_HASH = hex('b566a3eecce809896361988823cd2f423fe800e7')
const REFUND_LOCKTIME = 1800000000n

// Two distinct participants. `SOLVER` keeps the old fixture's key so the claim leaf stays
// diffable against the three-leaf generation; `TRADER` is new — the client refund key the RFQ
// family now requires.
const SOLVER = hex('1b84c5567b126440995d3ed5aaba0565d71e1834604819ff9c17f5e9d5dd078f')
const TRADER = hex('7c2a5ee7f0d4f5f61b0b6b1d4c9a83a0e2f5c6d7889a0b1c2d3e4f5061728394')

// P2TR pkScripts (0x5120 || x-only). `enforcePayTo` commits to the 32-byte key alone.
const TRADER_PK_SCRIPT = hex('512062c0a046dacce86ddd0343c6d3c7c79c2208ba0d9c9cf24a6d046d21d21f90f7')
const SOLVER_PK_SCRIPT = hex('51203b7f9c1e5a2d4068b1c3e5f7a9b0d2e4f60718293a4b5c6d7e8f90a1b2c3d4e5')

// The three-tier CSV ladder. Both sides derive it independently from the Arkade operator's own
// `unilateralExitDelay` — it is deliberately NOT carried on the RFQ wire — via
// The ladder both sides derive independently from the operator's exit delay: the base is rounded
// UP to a whole BIP68 512-second unit.
//
// The three leaves time three DIFFERENT parties' recourse, so they are not evenly spaced rungs.
// unilateralRefund needs sender AND receiver, so neither can spend it alone and separating it from
// the claim buys nothing. Only unilateralRefundWithoutReceiver is a solo path for the funder, so it
// is the only one whose timing can take money from a claimant who holds the preimage — and it gets
// real headroom, sized for an unroll broadcast per chain step with the server gone.
//
// Matches lightning-swap-service c904d44 (src/core/timelocks.ts) and ts-sdk 5ec2b719.
const GRANULARITY = 512
const SOLO_REFUND_HEADROOM = 8 * GRANULARITY
const UNILATERAL_CLAIM_DELAY = 4096
const UNILATERAL_REFUND_DELAY = UNILATERAL_CLAIM_DELAY
const UNILATERAL_REFUND_WITHOUT_RECEIVER_DELAY = UNILATERAL_CLAIM_DELAY + SOLO_REFUND_HEADROOM

const seconds = (value) => ({ type: 'seconds', value: BigInt(value) })

// Inputs for the optional covenant shapes below. Fixed, like everything else here: these vectors
// are a cross-implementation agreement check, not a fuzz corpus.
//
// The txid is in CANONICAL order — the leading 32 bytes of a serialized Asset ID. `VHTLC.ScriptV2`
// reverses it internally, because the introspection opcodes match wire order, and a port that
// reverses at the call site instead produces a covenant that always fails with nothing in the
// error naming why. That asymmetry is the reason an asset vector exists at all.
const ASSET_TXID = hex('4d1f8c2b7e05a9634f8d21c0ba97e3d5486f10729cab3e5d0817f4a26b93c0de')
const ASSET_GROUP_INDEX = 3
// Deliberately past OP_16, so the vector pins the script-number push and not just an OP_N byte.
const STRICT_AMOUNT = 25000n
const STRICT_ASSET_AMOUNT = 1234567n

// The corridor is entirely a question of which participant occupies which slot; the script
// construction is identical. On the send leg the trader funds and the solver claims with the
// preimage. On the receive leg the roles invert — the solver funds the Arkade side and the
// trader claims — so `sender`/`receiver` and the two covenant payout destinations swap with
// them. Getting this backwards produces a valid-looking address nobody can spend, which is
// exactly what these vectors exist to catch.
const corridors = {
  'arkade:BTC->lightning:BTC': {
    sender: TRADER,
    receiver: SOLVER,
    nonInteractiveClaimPkScript: SOLVER_PK_SCRIPT,
    nonInteractiveRefundPkScript: TRADER_PK_SCRIPT,
  },
  'lightning:BTC->arkade:BTC': {
    sender: SOLVER,
    receiver: TRADER,
    nonInteractiveClaimPkScript: TRADER_PK_SCRIPT,
    nonInteractiveRefundPkScript: SOLVER_PK_SCRIPT,
  },
}

const derive = ({ sender, receiver, nonInteractiveClaimPkScript, nonInteractiveRefundPkScript }) => {
  const script = new VHTLC.ScriptV2({
    sender,
    receiver,
    server: SERVER,
    preimageHash: PREIMAGE_HASH,
    refundLocktime: REFUND_LOCKTIME,
    unilateralClaimDelay: seconds(UNILATERAL_CLAIM_DELAY),
    unilateralRefundDelay: seconds(UNILATERAL_REFUND_DELAY),
    unilateralRefundWithoutReceiverDelay: seconds(UNILATERAL_REFUND_WITHOUT_RECEIVER_DELAY),
    nonInteractiveClaim: { receiverPkScript: nonInteractiveClaimPkScript, emulatorPubkey: EMULATOR },
    nonInteractiveRefund: { senderPkScript: nonInteractiveRefundPkScript, emulatorPubkey: EMULATOR },
  })

  return {
    inputs: {
      sender: toHex(sender),
      receiver: toHex(receiver),
      nonInteractiveClaimPkScript: toHex(nonInteractiveClaimPkScript),
      nonInteractiveRefundPkScript: toHex(nonInteractiveRefundPkScript),
    },
    // Taproot leaf order is load-bearing: it decides the merkle root and therefore the address.
    // This is the order `VHTLC.BaseScript` pushes them in, and the order a C# port must build.
    leaves: {
      claim: script.claimScript,
      refund: script.refundScript,
      refundWithoutReceiver: script.refundWithoutReceiverScript,
      unilateralClaim: script.unilateralClaimScript,
      unilateralRefund: script.unilateralRefundScript,
      unilateralRefundWithoutReceiver: script.unilateralRefundWithoutReceiverScript,
      nonInteractiveClaim: script.nonInteractiveClaimScript,
      nonInteractiveRefund: script.nonInteractiveRefundScript,
    },
    arkadeScripts: {
      nonInteractiveClaim: toHex(script.nonInteractiveClaimArkadeScript),
      nonInteractiveRefund: toHex(script.nonInteractiveRefundArkadeScript),
    },
    pkScript: toHex(script.pkScript),
  }
}

// The optional halves of the ladder, each on the send corridor's roles so a variant differs from
// `arkade:BTC->lightning:BTC` above in exactly the option it names. `VHTLC.Options` makes
// `nonInteractiveClaim` and `nonInteractiveRefund` independently optional, so the leaf count is 6,
// 7 or 8 — and each count is a different merkle root and therefore a different address. A port that
// hard-codes eight leaves derives the right address for the corridors and the wrong one for
// everything else, which is what these vectors exist to catch.
//
// `withoutReceiver`, which appends a ninth, is deliberately not covered: the .NET contract does not
// model it yet, and it lands in its own change.
const nic = (extra = {}) => ({
  receiverPkScript: SOLVER_PK_SCRIPT,
  emulatorPubkey: EMULATOR,
  ...extra,
})
const nir = (extra = {}) => ({
  senderPkScript: TRADER_PK_SCRIPT,
  emulatorPubkey: EMULATOR,
  ...extra,
})
const asset = { txid: ASSET_TXID, groupIndex: ASSET_GROUP_INDEX }

const variants = {
  // Neither covenant leaf: the plain six-leaf VHTLC, ScriptV2's preimage condition aside.
  'no-covenant': {},
  'claim-only': { nonInteractiveClaim: nic() },
  'refund-only': { nonInteractiveRefund: nir() },
  // Asset-denominated: only the covenant leaves change, and the sat clause is retained rather than
  // replaced.
  asset: {
    nonInteractiveClaim: nic(),
    nonInteractiveRefund: nir(),
    asset,
  },
  // The opt-in quoted bound, sats only. Refund leaves never take one: a refund returns what
  // arrived, so a quote has no place in it.
  'strict-sats': { nonInteractiveClaim: nic({ strict: { amount: STRICT_AMOUNT } }) },
  // ...and both bounds, which is the only shape an asset contract may ask for: strict on the sat
  // CARRIER alone would say nothing about the asset that is the actual amount.
  'strict-asset': {
    nonInteractiveClaim: nic({
      strict: { amount: STRICT_AMOUNT, assetAmount: STRICT_ASSET_AMOUNT },
    }),
    nonInteractiveRefund: nir(),
    asset,
  },
}

const deriveVariant = (options) => {
  const script = new VHTLC.ScriptV2({
    sender: TRADER,
    receiver: SOLVER,
    server: SERVER,
    preimageHash: PREIMAGE_HASH,
    refundLocktime: REFUND_LOCKTIME,
    unilateralClaimDelay: seconds(UNILATERAL_CLAIM_DELAY),
    unilateralRefundDelay: seconds(UNILATERAL_REFUND_DELAY),
    unilateralRefundWithoutReceiverDelay: seconds(UNILATERAL_REFUND_WITHOUT_RECEIVER_DELAY),
    ...options,
  })

  // Only the leaves this variant actually carries, in ladder order. An absent key is the vector
  // saying the leaf is not there, which is as much of the agreement as its bytes would be.
  const leaves = {
    claim: script.claimScript,
    refund: script.refundScript,
    refundWithoutReceiver: script.refundWithoutReceiverScript,
    unilateralClaim: script.unilateralClaimScript,
    unilateralRefund: script.unilateralRefundScript,
    unilateralRefundWithoutReceiver: script.unilateralRefundWithoutReceiverScript,
  }
  if (script.nonInteractiveClaimScript) leaves.nonInteractiveClaim = script.nonInteractiveClaimScript
  if (script.nonInteractiveRefundScript) leaves.nonInteractiveRefund = script.nonInteractiveRefundScript

  const arkadeScripts = {}
  if (script.nonInteractiveClaimArkadeScript) {
    arkadeScripts.nonInteractiveClaim = toHex(script.nonInteractiveClaimArkadeScript)
  }
  if (script.nonInteractiveRefundArkadeScript) {
    arkadeScripts.nonInteractiveRefund = toHex(script.nonInteractiveRefundArkadeScript)
  }

  return { leafCount: Object.keys(leaves).length, leaves, arkadeScripts, pkScript: toHex(script.pkScript) }
}

console.log(
  JSON.stringify(
    {
      _comment:
        'GENERATED — do not hand-edit. Produced by generate-covenant-vectors.mjs from the solver ' +
        "side's own VHTLC.ScriptV2 derivation. Treat as a safety gate, not a regression baseline: " +
        'if these disagree with our output, ours is what is wrong. Regenerate after every pull of ' +
        'the reference solver.',
      _sdk: sdkVersion,
      sharedInputs: {
        server: toHex(SERVER),
        emulatorPubkey: toHex(EMULATOR),
        preimageHash: toHex(PREIMAGE_HASH),
        refundLocktime: Number(REFUND_LOCKTIME),
        unilateralClaimDelay: UNILATERAL_CLAIM_DELAY,
        unilateralRefundDelay: UNILATERAL_REFUND_DELAY,
        unilateralRefundWithoutReceiverDelay: UNILATERAL_REFUND_WITHOUT_RECEIVER_DELAY,
        hrp: 'ark',
      },
      // `SIZE 32 EQUALVERIFY HASH160 <hash20> EQUAL` — ScriptV2's preimage condition. The size
      // gate is what separates it from `VHTLC.Script`, and it appears in every claim-family leaf.
      preimageCondition: toHex(
        Uint8Array.from([0x82, 0x01, 0x20, 0x88, 0xa9, 0x14, ...PREIMAGE_HASH, 0x87]),
      ),
      corridors: Object.fromEntries(
        Object.entries(corridors).map(([pair, roles]) => [pair, derive(roles)]),
      ),
      // Every variant uses the send corridor's roles and these destinations, so a variant's only
      // difference from `arkade:BTC->lightning:BTC` is the option it is named for.
      variantInputs: {
        sender: toHex(TRADER),
        receiver: toHex(SOLVER),
        nonInteractiveClaimPkScript: toHex(SOLVER_PK_SCRIPT),
        nonInteractiveRefundPkScript: toHex(TRADER_PK_SCRIPT),
        assetTxid: toHex(ASSET_TXID),
        assetGroupIndex: ASSET_GROUP_INDEX,
        strictAmount: Number(STRICT_AMOUNT),
        strictAssetAmount: Number(STRICT_ASSET_AMOUNT),
      },
      variants: Object.fromEntries(
        Object.entries(variants).map(([name, options]) => [name, deriveVariant(options)]),
      ),
    },
    null,
    2,
  ),
)

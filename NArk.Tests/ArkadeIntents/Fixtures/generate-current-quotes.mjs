import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { execFileSync } from 'node:child_process'

const root = resolve(process.argv[2])
const require = createRequire(pathToFileURL(resolve(root, 'package.json')))
const { CovenantSwapScript } = await import(pathToFileURL(require.resolve('@arkade-os/solver-arkade/arkade/covenant.js')))
const { deriveUnilateralDelays } = await import(pathToFileURL(require.resolve('@arkade-os/solver-core/core/timelocks.js')))
// The send leg stretches the solo rung to cover the horizon it just quoted, and publishes the
// result as `profile.refund_without_receiver_delay`. Pulling the solver's own function in is the
// point of this generator: a vector computed from our reading of the rule would pin the reading.
const { unilateralLadderFor } = await import(pathToFileURL(require.resolve('@arkade-os/solver-core/core/send.js')))
const { ripemd160 } = await import(pathToFileURL(require.resolve('@noble/hashes/legacy.js')))
const hex = value => Uint8Array.from(Buffer.from(value, 'hex'))
const server = 'e35799157be4b37565bb5afe4d04e6a0fa0a4b6a4f4e48b0d904685d253cdbdb'
const emulator = '999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9'
const solver = 'df5e3a677c20ff3af3c1701e5ed75aa7cc1e3ff8069ea4a8df5012494d7af6eb'
const client = '7c2a5ee7f0d4f5f61b0b6b1d4c9a83a0e2f5c6d7889a0b1c2d3e4f5061728394'
const solverScript = '5120ec250c5be12707c56bee7a263fb1495e0fbdb733c9eb35a53b5e57e1e2ec2534'
const clientScript = '5120535e2e7fb7a3fa9b3be74b13b813261497e3ad8a9d61cc45d233b4e0d21a7e73'
const delays = deriveUnilateralDelays(512)
// `quotedAt` is what a stretched vector needs and a base one must not have: the solver sizes the
// solo rung against `refundLocktime - quotedAt`, so a case without it is one where the base ladder
// stands, which is how the receive legs and every pre-0.3.0 send quote look.
const cases = [
  { name: 'send', paymentHash: 'ea7ad684b1ae3975cbbdab9512cd042ccbb5218636d6998262c41ed31693ecd9', refundLocktime: 1786552072, sender: client, receiver: solver, senderScript: solverScript, receiverScript: solverScript },
  { name: 'send-distinct', paymentHash: '22fa82b7a24a4907c3955ca1044b4eef7a4e30aab0dfa96eae8e6435500fb5f4', refundLocktime: 1786771492, sender: client, receiver: solver, senderScript: clientScript, receiverScript: solverScript },
  // Deliberately `send-distinct` with a horizon: the two vectors differ in the solo rung and in
  // nothing else, so an address that still matched would mean the delay never reached the script.
  { name: 'send-stretched', paymentHash: '22fa82b7a24a4907c3955ca1044b4eef7a4e30aab0dfa96eae8e6435500fb5f4', refundLocktime: 1786771492, quotedAt: 1786685092, sender: client, receiver: solver, senderScript: clientScript, receiverScript: solverScript },
  { name: 'receive', paymentHash: 'deb0e38ced1e41de6f92e70e80c418d2d356afaaa99e26f5939dbc7d3ef4772a', refundLocktime: 1786398939, sender: solver, receiver: client, senderScript: solverScript, receiverScript: clientScript },
]
const vectors = cases.map(input => {
  const ladder = input.quotedAt === undefined ? delays : unilateralLadderFor(delays, input.refundLocktime, input.quotedAt)
  const script = new CovenantSwapScript({
    server: hex(server), client: hex(input.sender), receiver: hex(input.receiver),
    preimageHash: ripemd160(hex(input.paymentHash)), refundLocktime: input.refundLocktime,
    claimDelay: ladder.unilateralClaimDelay, clientRefundDelay: ladder.unilateralRefundWithoutReceiverDelay,
    refundWithoutServerDelay: ladder.unilateralRefundDelay,
    nonInteractiveParameters: { emulatorPubkey: hex(emulator), senderPkScript: hex(input.senderScript), receiverPkScript: hex(input.receiverScript) },
  })
  // Emitted for every vector, stretched or not: it is what the quote publishes, so the test reads
  // it the same way the client does instead of branching on which kind of vector it holds.
  return { ...input, refundWithoutReceiverDelay: ladder.unilateralRefundWithoutReceiverDelay, address: script.address('tark', hex(server)).encode() }
})
console.log(JSON.stringify({ solverCommit: execFileSync('git', ['-C', root, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(), server, emulator, operatorExitDelay: 512, vectors }, null, 2))

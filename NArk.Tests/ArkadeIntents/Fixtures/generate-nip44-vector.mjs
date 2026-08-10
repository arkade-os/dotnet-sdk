// A golden vector for NIP-44 v2, generated with nostr-tools — the same library the reference
// solver's Nostr codec uses to seal directed RFQ traffic.
//
//   node NArk.Tests/ArkadeIntents/Fixtures/generate-nip44-vector.mjs \
//     <node-project> > NArk.Tests/ArkadeIntents/Fixtures/nip44.json
//
// Why a vector: our NIP-44 is a from-scratch implementation, down to a hand-written ChaCha20,
// because the platform exposes only the AEAD construction and NIP-44 uses the bare stream. Every
// piece of that — the conversation key, the padding buckets, the HKDF expansion, the MAC's
// associated data — is somewhere the two implementations could differ while both looking correct
// in isolation. The symptom would be a solver that silently ignores us, since a payload it cannot
// open is indistinguishable on a shared relay from one that was never addressed to it.
//
// The nonce is pinned so the ciphertext is comparable at all; in real use it is random per message.

import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const modulesPath = process.argv[2]
if (!modulesPath) {
  console.error('usage: generate-nip44-vector.mjs <node-project-with-the-required-npm-deps>')
  process.exit(2)
}

const require = createRequire(pathToFileURL(resolve(modulesPath, 'package.json')))
// The package's main entry does not re-export the nip44 namespace; the ESM index does.
const nostrToolsRoot = require.resolve('nostr-tools').replace(/\/lib\/.*$/, '')
const { nip44 } = await import(pathToFileURL(resolve(nostrToolsRoot, 'lib/esm/index.js')).href)
const { schnorr } = await import(pathToFileURL(require.resolve('@noble/curves/secp256k1.js')).href)
const { finalizeEvent, getEventHash, verifyEvent } = await import(
  pathToFileURL(resolve(nostrToolsRoot, 'lib/esm/index.js')).href
)

const hexToBytes = (s) => Uint8Array.from(Buffer.from(s, 'hex'))
const toHex = (b) => Buffer.from(b).toString('hex')

// Fixed on both sides so the whole derivation is reproducible.
const CLIENT_PRIV = hexToBytes('11'.repeat(32))
const SOLVER_PRIV = hexToBytes('22'.repeat(32))
const NONCE = hexToBytes('33'.repeat(32))

const clientPub = schnorr.getPublicKey(CLIENT_PRIV)
const solverPub = schnorr.getPublicKey(SOLVER_PRIV)

// Symmetric: each side derives it from its own secret and the other's public key.
const fromClient = nip44.v2.utils.getConversationKey(CLIENT_PRIV, toHex(solverPub))
const fromSolver = nip44.v2.utils.getConversationKey(SOLVER_PRIV, toHex(clientPub))
if (toHex(fromClient) !== toHex(fromSolver)) {
  console.error('conversation key is not symmetric — nostr-tools changed shape')
  process.exit(1)
}

// Shaped like the traffic this actually carries, rather than a toy string.
const plaintext = JSON.stringify({
  v: 1,
  type: 'rfq_request',
  rfq_id: 'a'.repeat(64),
  pair: 'arkade:BTC->lightning:BTC',
  amount_side: 'to',
  profile: { invoice: 'lnbcrt500000n1p...', refund_address: 'tark1qexample', client_refund_pubkey: 'b'.repeat(64) },
})

console.log(
  JSON.stringify(
    {
      _comment:
        'GENERATED — do not hand-edit. Run generate-nip44-vector.mjs. Produced with nostr-tools, ' +
        'the library the reference solver seals its directed traffic with. If our output disagrees, ' +
        'ours is what is wrong.',
      inputs: {
        clientPrivateKey: toHex(CLIENT_PRIV),
        clientPublicKey: toHex(clientPub),
        solverPrivateKey: toHex(SOLVER_PRIV),
        solverPublicKey: toHex(solverPub),
        nonce: toHex(NONCE),
        plaintext,
      },
      conversationKey: toHex(fromClient),
      // Padding buckets, which decide the ciphertext length and so must agree exactly.
      paddedLengths: Object.fromEntries(
        [1, 16, 32, 33, 100, 256, 257, 1000].map((n) => [n, nip44.v2.utils.calcPaddedLen(n)]),
      ),
      payload: nip44.v2.encrypt(plaintext, fromClient, NONCE),
      // A directed RFQ event as the reference produces it: kind 4859, sealed content, `p`-tagged.
      // Our id computation and signature check are pinned against this.
      event: (() => {
        const signed = finalizeEvent(
          {
            kind: 4859,
            created_at: 1786000000,
            tags: [['p', toHex(solverPub)]],
            content: nip44.v2.encrypt(plaintext, fromClient, NONCE),
          },
          CLIENT_PRIV,
        )
        if (!verifyEvent(signed)) throw new Error('nostr-tools produced an event it cannot verify')
        if (getEventHash(signed) !== signed.id) throw new Error('id mismatch from nostr-tools')
        return signed
      })(),
    },
    null,
    2,
  ),
)

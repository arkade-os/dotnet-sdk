// A broad ChaCha20 vector set, generated against @noble/ciphers — the implementation nostr-tools
// (and therefore the reference solver) seals NIP-44 payloads with.
//
//   node NArk.Tests/ArkadeIntents/Fixtures/generate-chacha20-vectors.mjs \
//     <node-project> > NArk.Tests/ArkadeIntents/Fixtures/chacha20.json
//
// RFC 8439's Appendix A.2 vectors are merged into the same file by a companion step (they are
// parsed from the published RFC text, not retyped) and are the authority; this generated set exists
// for BREADTH. A hand-written ChaCha20 tends to fail on the seams rather than in the core: the block
// counter advancing, a non-zero or wrapping initial counter, a truncated final block, an empty
// input. Each of those yields output that is correct for the one length someone happened to test
// and silently wrong elsewhere, which is the worst failure shape for a cipher — the ciphertext
// still looks like noise.
//
// So the lengths cluster deliberately around 64-byte boundaries, and the counters include 2^32-2
// so that a run crossing 2^32 is covered.

import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const modulesPath = process.argv[2]
if (!modulesPath) {
  console.error('usage: generate-chacha20-vectors.mjs <node-project-with-the-required-npm-deps>')
  process.exit(2)
}

const require = createRequire(pathToFileURL(resolve(modulesPath, 'package.json')))
// Reached through the store path: it is a transitive dependency, so it has no top-level entry.
const noblePath = require
  .resolve('nostr-tools')
  .replace(/node_modules[/].*$/, 'node_modules/.pnpm/@noble+ciphers@2.1.1/node_modules/@noble/ciphers/chacha.js')
const { chacha20 } = await import(pathToFileURL(noblePath).href)

const toHex = (b) => Buffer.from(b).toString('hex')

// Deterministic pseudo-random material, so regenerating produces the same file.
let seed = 0x2545f491
const nextByte = () => {
  seed ^= seed << 13; seed >>>= 0
  seed ^= seed >> 17
  seed ^= seed << 5; seed >>>= 0
  return seed & 0xff
}
const bytes = (n) => Uint8Array.from({ length: n }, nextByte)

// Around every 64-byte seam, plus 0 and a few long ones.
const LENGTHS = [
  0, 1, 2, 15, 16, 31, 32, 33, 63, 64, 65, 66, 100, 127, 128, 129, 191, 192, 193,
  255, 256, 257, 320, 511, 512, 513, 1000, 1024, 1025, 4096,
]

// 0 is NIP-44's convention, 1 is the RFC's AEAD convention, and the last reaches the highest block
// the 32-bit counter can address.
const COUNTERS = [0, 1, 2, 255, 256, 65535, 0xfffffffe]

// The reference refuses the last representable value outright, so a run may end at it but not use
// it as a starting block.
const MAX_COUNTER = 2 ** 32 - 1

const vectors = []
for (const length of LENGTHS) {
  for (const counter of COUNTERS) {
    // The counter is 32 bits and RFC 8439 does not define what happens past the end, so a run that
    // would carry it over is out of scope rather than a case worth pinning.
    const blocks = Math.ceil(length / 64) || 1
    if (counter + blocks > MAX_COUNTER) continue

    const key = bytes(32)
    const nonce = bytes(12)
    const plaintext = bytes(length)
    vectors.push({
      key: toHex(key),
      nonce: toHex(nonce),
      counter,
      plaintext: toHex(plaintext),
      ciphertext: toHex(chacha20(key, nonce, plaintext, undefined, counter)),
    })
  }
}

console.log(
  JSON.stringify(
    {
      _comment:
        'GENERATED — do not hand-edit. Run generate-chacha20-vectors.mjs. Cross-checked against ' +
        '@noble/ciphers, the implementation the reference solver seals NIP-44 with. RFC 8439 is the ' +
        'authority and is asserted separately; this set is for breadth around the seams.',
      _source: '@noble/ciphers@2.1.1 chacha20',
      count: vectors.length,
      vectors,
    },
    null,
    2,
  ),
)

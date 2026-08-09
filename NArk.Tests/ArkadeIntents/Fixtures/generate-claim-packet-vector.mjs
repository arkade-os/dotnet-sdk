// A golden vector for the receive legs' ECIES claim packet, generated with the same primitives
// the counterparty's reference client uses (`test/e2e/support/claimPacket.ts` in
// the reference solver, whose header documents where the scheme was recovered from).
//
//   node NArk.Tests/ArkadeIntents/Fixtures/generate-claim-packet-vector.mjs \
//     <node-project> > NArk.Tests/ArkadeIntents/Fixtures/claim_packet.json
//
// Why a vector rather than a round-trip test: only covclaimd holds the key that opens this, so we
// cannot decrypt our own output to check it. What we can do is fix every input — ephemeral key and
// nonce included, which the real API never lets a caller do — and require our bytes to equal the
// reference implementation's. That catches the one mistake this scheme invites: the ECDH shared
// secret is the 32-byte X coordinate, and keeping the compressed point's parity byte still produces
// a well-formed key on both sides. Nothing local disagrees; only a live daemon's AEAD tag check
// does, by which point a swap is already in flight.

import { createCipheriv } from 'node:crypto'
import { createRequire } from 'node:module'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const modulesPath = process.argv[2]
if (!modulesPath) {
  console.error('usage: generate-claim-packet-vector.mjs <node-project-with-the-required-npm-deps>')
  process.exit(2)
}

const require = createRequire(pathToFileURL(resolve(modulesPath, 'package.json')))
const importFrom = (spec) => import(pathToFileURL(require.resolve(spec)).href)

const { secp256k1 } = await importFrom('@noble/curves/secp256k1.js')
const { hkdf } = await importFrom('@noble/hashes/hkdf.js')
const { sha256 } = await importFrom('@noble/hashes/sha2.js')

const hexToBytes = (s) => Uint8Array.from(Buffer.from(s, 'hex'))
const toHex = (b) => Buffer.from(b).toString('hex')

// Fixed inputs. The ephemeral key and nonce are normally generated per packet — pinning them is
// the only way to make the output comparable at all.
const PREIMAGE = hexToBytes('11'.repeat(32))
const EPHEMERAL_PRIV = hexToBytes('2b'.repeat(32))
const COVCLAIMD_PRIV = hexToBytes('5c'.repeat(32))
const NONCE = hexToBytes('0102030405060708090a0b0c')

const HKDF_INFO = new TextEncoder().encode('covclaimd/preimage/v1')

const covclaimdPub = secp256k1.getPublicKey(COVCLAIMD_PRIV, true)
const ephemeralPub = secp256k1.getPublicKey(EPHEMERAL_PRIV, true)

// X coordinate only — see this file's header.
const shared = secp256k1.getSharedSecret(EPHEMERAL_PRIV, covclaimdPub, true).slice(1)
const key = hkdf(sha256, shared, ephemeralPub, HKDF_INFO, 32)

const cipher = createCipheriv('aes-256-gcm', key, NONCE)
cipher.setAAD(ephemeralPub)
const sealed = Buffer.concat([cipher.update(PREIMAGE), cipher.final(), cipher.getAuthTag()])
const wire = Buffer.concat([Buffer.from(ephemeralPub), Buffer.from(NONCE), sealed])

console.log(
  JSON.stringify(
    {
      _comment:
        'GENERATED — do not hand-edit. Run generate-claim-packet-vector.mjs. If our output disagrees ' +
        'with this, ours is what is wrong.',
      inputs: {
        preimage: toHex(PREIMAGE),
        ephemeralPrivateKey: toHex(EPHEMERAL_PRIV),
        covclaimdPublicKey: toHex(covclaimdPub),
        nonce: toHex(NONCE),
      },
      intermediates: {
        ephemeralPublicKey: toHex(ephemeralPub),
        sharedSecretX: toHex(shared),
        derivedKey: toHex(key),
      },
      packet: wire.toString('base64'),
      paymentHash: toHex(sha256(PREIMAGE)),
    },
    null,
    2,
  ),
)

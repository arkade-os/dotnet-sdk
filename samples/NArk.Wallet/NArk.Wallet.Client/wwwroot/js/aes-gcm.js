// AES-256-GCM via WebCrypto, for the one primitive the browser's .NET runtime does not have.
//
// Values cross as base64 rather than as byte arrays: it keeps the interop surface to strings,
// which marshal the same way on every Blazor version, and the payloads here are tens of bytes.
//
// crypto.subtle.encrypt returns ciphertext with the authentication tag already appended, which is
// the layout the caller wants and the same one Go's aead.Seal produces. Nothing is rearranged here
// — a difference in this file would be invisible until a live daemon failed to open a packet.

const decode = (b64) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
const encode = (bytes) => btoa(String.fromCharCode(...new Uint8Array(bytes)));

export async function encrypt(keyB64, nonceB64, plaintextB64, associatedDataB64) {
    const key = await crypto.subtle.importKey(
        'raw', decode(keyB64), { name: 'AES-GCM' }, false, ['encrypt']);

    const sealed = await crypto.subtle.encrypt(
        {
            name: 'AES-GCM',
            iv: decode(nonceB64),
            additionalData: decode(associatedDataB64),
            // 128 bits: the 16-byte tag the wire format and the Go implementation both assume.
            tagLength: 128,
        },
        key,
        decode(plaintextB64));

    return encode(sealed);
}

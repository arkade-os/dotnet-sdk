namespace NArk.Arkade.Scripts;

/// <summary>
/// Arkade Script extension opcodes — the introspection / arithmetic / asset / EC
/// helpers that ArkadeScript adds on top of standard Bitcoin Script.
/// </summary>
/// <remarks>
/// <para>
/// This enum is a 1:1 mirror of the ts-sdk <c>ARKADE_OP</c> table (and behind it,
/// the introspector reference at <c>arkade-os/introspector pkg/arkade/opcode.go</c>).
/// Byte values MUST stay in lockstep with both — scripts produced by one SDK are
/// consumed by the introspector and the other SDKs, so a divergence here breaks
/// cross-SDK compatibility.
/// </para>
/// <para>
/// The values fall in <c>0xb3</c> (repurposed NOP4 slot) and <c>0xc4</c>–<c>0xf3</c>;
/// standard Bitcoin opcodes are emitted via NBitcoin's <see cref="NBitcoin.Op"/>
/// type and are NOT re-declared here.
/// </para>
/// </remarks>
public enum ArkadeOpcode : byte
{
    /// <summary>0xb3 — Verify a Merkle branch (repurposed NOP4 slot).</summary>
    OP_MERKLEBRANCHVERIFY = 0xb3,

    // ─── SHA-256 streaming (0xc4–0xc6) ─────────────────────────────

    /// <summary>0xc4 — Initialise a streaming SHA-256 state.</summary>
    OP_SHA256INITIALIZE = 0xc4,
    /// <summary>0xc5 — Feed bytes into a streaming SHA-256 state.</summary>
    OP_SHA256UPDATE = 0xc5,
    /// <summary>0xc6 — Finalise a streaming SHA-256 state, push the digest.</summary>
    OP_SHA256FINALIZE = 0xc6,

    // ─── Input introspection (0xc7–0xcb) ───────────────────────────

    /// <summary>0xc7 — Push the outpoint of an input by index.</summary>
    OP_INSPECTINPUTOUTPOINT = 0xc7,
    /// <summary>0xc8 — Push the ArkadeScript hash of an input by index.</summary>
    OP_INSPECTINPUTARKADESCRIPTHASH = 0xc8,
    /// <summary>0xc9 — Push the value (sats) of an input by index.</summary>
    OP_INSPECTINPUTVALUE = 0xc9,
    /// <summary>0xca — Push the scriptPubKey of an input by index.</summary>
    OP_INSPECTINPUTSCRIPTPUBKEY = 0xca,
    /// <summary>0xcb — Push the nSequence of an input by index.</summary>
    OP_INSPECTINPUTSEQUENCE = 0xcb,

    // ─── Signatures (0xcc–0xcd) ────────────────────────────────────

    /// <summary>0xcc — Verify a Schnorr signature against an arbitrary message + pubkey.</summary>
    OP_CHECKSIGFROMSTACK = 0xcc,
    /// <summary>0xcd — Push the index of the input currently being executed.</summary>
    OP_PUSHCURRENTINPUTINDEX = 0xcd,

    // ─── Input arkade-witness introspection (0xce) ─────────────────

    /// <summary>0xce — Push the ArkadeScriptWitness hash of an input by index.</summary>
    OP_INSPECTINPUTARKADEWITNESSHASH = 0xce,

    // ─── Output introspection (0xcf, 0xd1) ─────────────────────────

    /// <summary>0xcf — Push the value (sats) of an output by index.</summary>
    OP_INSPECTOUTPUTVALUE = 0xcf,
    /// <summary>0xd1 — Push the scriptPubKey of an output by index.</summary>
    OP_INSPECTOUTPUTSCRIPTPUBKEY = 0xd1,

    // ─── Transaction introspection (0xd2–0xd6) ─────────────────────

    /// <summary>0xd2 — Push the transaction nVersion.</summary>
    OP_INSPECTVERSION = 0xd2,
    /// <summary>0xd3 — Push the transaction nLockTime.</summary>
    OP_INSPECTLOCKTIME = 0xd3,
    /// <summary>0xd4 — Push the number of inputs.</summary>
    OP_INSPECTNUMINPUTS = 0xd4,
    /// <summary>0xd5 — Push the number of outputs.</summary>
    OP_INSPECTNUMOUTPUTS = 0xd5,
    /// <summary>0xd6 — Push the transaction weight.</summary>
    OP_TXWEIGHT = 0xd6,

    // ─── 64-bit arithmetic (0xd7–0xdf) ─────────────────────────────

    /// <summary>0xd7 — 64-bit signed addition with overflow check.</summary>
    OP_ADD64 = 0xd7,
    /// <summary>0xd8 — 64-bit signed subtraction with overflow check.</summary>
    OP_SUB64 = 0xd8,
    /// <summary>0xd9 — 64-bit signed multiplication with overflow check.</summary>
    OP_MUL64 = 0xd9,
    /// <summary>0xda — 64-bit signed division (quotient and remainder).</summary>
    OP_DIV64 = 0xda,
    /// <summary>0xdb — 64-bit signed negation.</summary>
    OP_NEG64 = 0xdb,
    /// <summary>0xdc — 64-bit signed less-than comparison.</summary>
    OP_LESSTHAN64 = 0xdc,
    /// <summary>0xdd — 64-bit signed less-than-or-equal comparison.</summary>
    OP_LESSTHANOREQUAL64 = 0xdd,
    /// <summary>0xde — 64-bit signed greater-than comparison.</summary>
    OP_GREATERTHAN64 = 0xde,
    /// <summary>0xdf — 64-bit signed greater-than-or-equal comparison.</summary>
    OP_GREATERTHANOREQUAL64 = 0xdf,

    // ─── Conversion (0xe0–0xe2) ────────────────────────────────────

    /// <summary>0xe0 — Convert a script-num on the stack to a little-endian 64-bit number.</summary>
    OP_SCRIPTNUMTOLE64 = 0xe0,
    /// <summary>0xe1 — Convert a little-endian 64-bit number on the stack to a script-num.</summary>
    OP_LE64TOSCRIPTNUM = 0xe1,
    /// <summary>0xe2 — Sign-extend a little-endian 32-bit number to a little-endian 64-bit number.</summary>
    OP_LE32TOLE64 = 0xe2,

    // ─── EC operations (0xe3–0xe4) ─────────────────────────────────

    /// <summary>0xe3 — Verify EC scalar multiplication: <c>scalar · G ?= expected_point</c>.</summary>
    OP_ECMULSCALARVERIFY = 0xe3,
    /// <summary>0xe4 — Verify a tweaked public key: <c>internal + hash · G ?= tweaked</c>.</summary>
    OP_TWEAKVERIFY = 0xe4,

    // ─── Asset groups (0xe5–0xf2) ──────────────────────────────────

    /// <summary>0xe5 — Push the number of asset groups in the current Arkade transaction.</summary>
    OP_INSPECTNUMASSETGROUPS = 0xe5,
    /// <summary>0xe6 — Push the asset ID of an asset group by index.</summary>
    OP_INSPECTASSETGROUPASSETID = 0xe6,
    /// <summary>0xe7 — Push the control-asset reference of an asset group by index.</summary>
    OP_INSPECTASSETGROUPCTRL = 0xe7,
    /// <summary>0xe8 — Find an asset group by asset ID.</summary>
    OP_FINDASSETGROUPBYASSETID = 0xe8,
    /// <summary>0xe9 — Push the metadata-Merkle-root of an asset group by index.</summary>
    OP_INSPECTASSETGROUPMETADATAHASH = 0xe9,
    /// <summary>0xea — Push the number of inputs/outputs in an asset group by index.</summary>
    OP_INSPECTASSETGROUPNUM = 0xea,
    /// <summary>0xeb — Push the full asset group struct by index.</summary>
    OP_INSPECTASSETGROUP = 0xeb,
    /// <summary>0xec — Push the per-asset-group sum of inputs/outputs.</summary>
    OP_INSPECTASSETGROUPSUM = 0xec,
    /// <summary>0xed — Push the count of assets attached to an output by index.</summary>
    OP_INSPECTOUTASSETCOUNT = 0xed,
    /// <summary>0xee — Push an asset attached to an output by (output index, asset index).</summary>
    OP_INSPECTOUTASSETAT = 0xee,
    /// <summary>0xef — Look up an asset attached to an output by asset ID.</summary>
    OP_INSPECTOUTASSETLOOKUP = 0xef,
    /// <summary>0xf0 — Push the count of assets attached to an input by index.</summary>
    OP_INSPECTINASSETCOUNT = 0xf0,
    /// <summary>0xf1 — Push an asset attached to an input by (input index, asset index).</summary>
    OP_INSPECTINASSETAT = 0xf1,
    /// <summary>0xf2 — Look up an asset attached to an input by asset ID.</summary>
    OP_INSPECTINASSETLOOKUP = 0xf2,

    // ─── Transaction id (0xf3) ─────────────────────────────────────

    /// <summary>0xf3 — Push the txid of the current transaction.</summary>
    OP_TXID = 0xf3,
}

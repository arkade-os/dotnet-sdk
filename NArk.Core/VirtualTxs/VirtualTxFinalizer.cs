using NArk.Abstractions.Helpers;
using NBitcoin;

namespace NArk.Core.VirtualTxs;

/// <summary>
/// Turns a virtual-tx PSBT (as served by arkd's <c>GetVirtualTxs</c>) into a
/// signed, broadcastable <see cref="Transaction"/> by assembling each input's
/// witness from the PSBT's own fields — the aggregated key-path signature, or an
/// Arkade script-path witness (leaf script + control block + condition witness +
/// per-key script-spend sigs). Mirrors arkd's <c>redeem.go</c> / <c>finalizer.go</c>.
/// <para>
/// The distinction that matters for persistence: <see cref="IsBroadcastReady"/>
/// tells whether a stored copy is <b>fully signed</b> (every input resolvable from
/// PSBT fields alone), so callers can avoid freezing a sig-less template — a
/// preconfirmed VTXO's tree ancestor can be served sig-less right after a batch
/// and only carries the MuSig2 signature once finalization has propagated.
/// </para>
/// </summary>
public static class VirtualTxFinalizer
{
    /// <summary>
    /// True when every input of the PSBT can be finalized from its own fields
    /// (key-path sig / Arkade script-path / an already-present FinalScriptWitness) —
    /// i.e. the stored copy is broadcast-ready and needs no operator refetch.
    /// Returns false for a sig-less template, and for any parse failure.
    /// </summary>
    /// <remarks>
    /// Only the signature fields are inspected (which are network-independent),
    /// so parsing uses a fixed network — the caller doesn't need to know the
    /// wallet's network just to answer "is this signed?". The actual broadcast
    /// path (<see cref="Parse"/>) still parses with the real network.
    /// </remarks>
    public static bool IsBroadcastReady(string hex)
    {
        try
        {
            return TryAssembleFromFields(hex, Network.Main, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses and finalizes the virtual tx. Prefers per-input witness assembly
    /// from PSBT fields; falls back to NBitcoin's standard PSBT finalize (lifting
    /// whatever <c>FinalScriptWitness</c>/<c>FinalScriptSig</c> arkd populated)
    /// only when a field-driven witness can't be built for every input.
    /// </summary>
    public static Transaction Parse(string hex, Network network)
    {
        if (TryAssembleFromFields(hex, network, out var assembled))
            return assembled!;

        // Fallback: standard PSBT finalize. If the PSBT lacks witness_utxo it'll
        // throw — lift whatever FinalScriptWitness/Sig arkd populated instead.
        var psbt = PSBT.Parse(hex, network);
        try
        {
            psbt.Finalize();
            return psbt.ExtractTransaction();
        }
        catch (PSBTException)
        {
            var fallbackTx = psbt.GetGlobalTransaction();
            for (var i = 0; i < psbt.Inputs.Count && i < fallbackTx.Inputs.Count; i++)
            {
                var psbtInput = psbt.Inputs[i];
                if (psbtInput.FinalScriptWitness is not null)
                    fallbackTx.Inputs[i].WitScript = psbtInput.FinalScriptWitness;
                if (psbtInput.FinalScriptSig is not null)
                    fallbackTx.Inputs[i].ScriptSig = psbtInput.FinalScriptSig;
            }
            return fallbackTx;
        }
    }

    /// <summary>
    /// Assembles every input's witness from PSBT fields (cf. arkd redeem.go /
    /// finalizer.go): key-path sig → aggregated-sig witness; else Arkade
    /// script-path → <see cref="TryFinalizeVtxoScript"/>; else lift an existing
    /// FinalScriptWitness. Returns false (leaving <paramref name="tx"/> null) as
    /// soon as any input can't be resolved — the caller then decides whether to
    /// fall back or treat the copy as not-yet-signed.
    /// </summary>
    private static bool TryAssembleFromFields(string hex, Network network, out Transaction? tx)
    {
        tx = null;
        var psbt = PSBT.Parse(hex, network);
        var built = psbt.GetGlobalTransaction();

        for (var i = 0; i < psbt.Inputs.Count && i < built.Inputs.Count; i++)
        {
            var input = psbt.Inputs[i];

            if (input.TaprootKeySignature is not null)
            {
                // The `true` flag tells NBitcoin these bytes are stack pushes,
                // not a pre-serialized witness — same idiom used elsewhere.
                built.Inputs[i].WitScript = new WitScript(new[] { input.TaprootKeySignature.ToBytes() }, true);
            }
            else if (TryFinalizeVtxoScript(input, out var scriptWitness))
            {
                built.Inputs[i].WitScript = scriptWitness;
            }
            else if (input.FinalScriptWitness is not null)
            {
                built.Inputs[i].WitScript = input.FinalScriptWitness;
            }
            else
            {
                return false;
            }
        }

        tx = built;
        return true;
    }

    /// <summary>
    /// Assembles the taproot script-path witness for a leaf Arkade tx from its
    /// PSBT fields, mirroring arkd's <c>FinalizeVtxoScript</c> (ark-lib
    /// <c>finalizer.go</c> / <c>closure.go</c>). Raw field accessors live in
    /// <see cref="PsbtHelpers"/>.
    /// </summary>
    private static bool TryFinalizeVtxoScript(PSBTInput input, out WitScript witness)
    {
        witness = WitScript.Empty;

        if (!input.TryGetTaprootLeafScript(out var controlBlock, out var leafScript))
            return false;

        var signatures = input.GetTaprootScriptSpendSignatures();
        if (signatures.Count == 0)
            return false;

        var pubKeys = ExtractCheckSigPubKeys(leafScript);
        if (pubKeys.Count == 0)
            return false;

        var stack = new List<byte[]>();

        // A condition witness (present only for condition closures) satisfies the
        // script's condition prefix and sits at the bottom of the stack.
        var condition = input.GetArkFieldConditionWitness();
        if (condition is not null)
            stack.AddRange(condition.Pushes);

        // Signatures are pushed in reverse public-key order — see
        // MultisigClosure.Witness in arkd's closure.go.
        for (var i = pubKeys.Count - 1; i >= 0; i--)
        {
            if (!signatures.TryGetValue(pubKeys[i], out var sig))
                return false; // a required signature is missing; cannot finalize
            stack.Add(sig);
        }

        stack.Add(leafScript.ToBytes());
        stack.Add(controlBlock);

        witness = new WitScript(stack.ToArray());
        return true;
    }

    // Returns the 32-byte x-only public keys immediately consumed by
    // OP_CHECKSIG / OP_CHECKSIGVERIFY, in script order. Non-key 32-byte pushes
    // (e.g. a hashlock preimage hash) are skipped because they are not followed
    // by a checksig opcode.
    private static List<string> ExtractCheckSigPubKeys(Script leafScript)
    {
        var ops = leafScript.ToOps().ToList();
        var pubKeys = new List<string>();
        for (var i = 0; i + 1 < ops.Count; i++)
        {
            if (ops[i].PushData is { Length: 32 } push &&
                ops[i + 1].Code is OpcodeType.OP_CHECKSIG or OpcodeType.OP_CHECKSIGVERIFY)
            {
                pubKeys.Add(Convert.ToHexString(push).ToLowerInvariant());
            }
        }

        return pubKeys;
    }
}

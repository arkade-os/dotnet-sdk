using NArk.Abstractions;
using NArk.Abstractions.Helpers;
using NArk.Arkade.Scripts;
using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Arkade.Emulator;

/// <summary>
/// Helpers that link the existing <see cref="ArkCoin"/> + PSBT spend flow
/// to the emulator co-signing service. The integration points:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <description>
///     <see cref="BuildEmulatorPackets"/> — produces the
///     <see cref="EmulatorPacket"/>(s) pinning each arkade-bound input's script
///     bytes + witness, for the generic spend path to merge into the single
///     Extension OP_RETURN. That output must be appended to the tx <em>before</em>
///     any input is signed, since signatures commit to the full output set.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="CoSignWithEmulatorAsync"/> — submits the partially-
///     signed PSBT to the emulator and returns the PSBT with the
///     emulator's signatures added. Call after the user signer has
///     attached its own partial sigs.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="AttachPrevArkTxsAsync"/> / <see cref="AttachIntentPrevArkTxsAsync"/> —
///     annotate each input with the transaction that funded it, which the emulator
///     requires on every submitted input. These write to the PSBT's <c>unknown</c> map,
///     so they run <em>after</em> signing, just before submission.
///     </description>
///   </item>
/// </list>
/// <para>
/// Detection is type-driven via <see cref="IArkadeBoundScriptBuilder"/> —
/// any <see cref="ArkCoin"/> whose <c>SpendingScriptBuilder</c> implements
/// the interface is treated as arkade-bound. Spends that mix arkade and
/// non-arkade inputs are supported: only the arkade-bound inputs become
/// entries in the EmulatorPacket.
/// </para>
/// </remarks>
public static class ArkadePsbtExtensions
{
    /// <summary>
    /// True if the spend uses at least one arkade-bound coin and therefore
    /// needs both the EmulatorPacket OP_RETURN attachment and the
    /// post-sign emulator REST round-trip.
    /// </summary>
    public static bool RequiresEmulatorCoSigning(IEnumerable<ArkCoin> coins)
    {
        ArgumentNullException.ThrowIfNull(coins);
        return coins.Any(c => c.SpendingScriptBuilder is IArkadeBoundScriptBuilder);
    }

    /// <summary>
    /// Build the <see cref="EmulatorPacket"/>(s) for the arkade-bound inputs of a
    /// spend, without wrapping them in an Extension/OP_RETURN — so the generic
    /// spend path (<c>NArk.Core</c>) can merge them with the asset packet into a
    /// single Extension via <see cref="NArk.Core.Assets.ISpendExtensionPacketProvider"/>.
    /// Returns an empty list when no input is arkade-bound.
    /// </summary>
    /// <param name="coinsByVin">
    /// The spend inputs in transaction-input-index order — index <c>i</c> in this
    /// list corresponds to <c>vin = i</c> on the resulting tx.
    /// </param>
    public static IReadOnlyList<IExtensionPacket> BuildEmulatorPackets(IReadOnlyList<ArkCoin> coinsByVin)
    {
        ArgumentNullException.ThrowIfNull(coinsByVin);

        var entries = new List<EmulatorEntry>();
        for (var vin = 0; vin < coinsByVin.Count; vin++)
        {
            if (coinsByVin[vin].SpendingScriptBuilder is not IArkadeBoundScriptBuilder arkade) continue;
            // The emulator's witness is the arkade-script witness carried on the builder — NOT the
            // coin's on-chain SpendingConditionWitness (that one satisfies the tapscript condition).
            var witness = ExtractWitnessPushes(arkade.ArkadeScriptWitness);
            entries.Add(new EmulatorEntry((ushort)vin, arkade.ArkadeScript, witness));
        }

        return entries.Count == 0 ? [] : [new EmulatorPacket(entries)];
    }

    /// <summary>
    /// Submit a partially-signed PSBT (already carrying the user's sigs and
    /// the EmulatorPacket OP_RETURN output) to the emulator and
    /// return the PSBT with the emulator's signatures merged in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The emulator signs only inputs whose attached scripts pass its
    /// validation; non-arkade inputs are passed through untouched. The
    /// returned PSBT is the union of (input PSBT) + (emulator partial
    /// sigs) — assembled server-side, so this method is a thin wrapper over
    /// <see cref="IEmulatorProvider.SubmitTxAsync"/>.
    /// </para>
    /// <para>
    /// Deliberately carries no checkpoint parameter: it would only be able to return the
    /// co-signed Arkade transaction, silently dropping the emulator's <c>signed_checkpoint_txs</c>.
    /// Callers that submit checkpoints — <see cref="ArkadeEmulatorSpendSubmitter"/> — call
    /// <see cref="IEmulatorProvider.SubmitTxAsync"/> directly and consume both halves.
    /// </para>
    /// </remarks>
    /// <param name="psbt">PSBT with user partial sigs already attached.</param>
    /// <param name="emulator">Provider client for the configured emulator instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The co-signed Arkade transaction PSBT.</returns>
    /// <exception cref="InvalidOperationException">
    /// The emulator returned no signed Arkade transaction, so the input was not co-signed.
    /// </exception>
    public static async Task<PSBT> CoSignWithEmulatorAsync(
        this PSBT psbt,
        IEmulatorProvider emulator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(psbt);
        ArgumentNullException.ThrowIfNull(emulator);

        var resp = await emulator.SubmitTxAsync(
            psbt.ToBase64(),
            Array.Empty<string>(),
            cancellationToken);

        // Same guard as ArkadeEmulatorSpendSubmitter: an empty response means the input was
        // not co-signed. Without it PSBT.Parse throws a bare FormatException that says nothing
        // about which call failed or why.
        if (string.IsNullOrEmpty(resp.SignedArkTx))
        {
            throw new InvalidOperationException(
                "Emulator returned no signed Arkade transaction — the input was not co-signed.");
        }

        // The emulator returns a PSBT that's the union of the input PSBT
        // (so user sigs are preserved) plus its own partial sigs. We can take
        // the response wholesale and parse it on the caller's network.
        return PSBT.Parse(resp.SignedArkTx, psbt.Network);
    }

    /// <summary>
    /// Attaches the <c>prevarktx</c> ark field to every input of an Arkade transaction bound
    /// for the emulator's <c>POST /v1/tx</c>, resolving each previous transaction through
    /// <paramref name="prevArkTxProvider"/>. Call after signing and immediately before submitting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emulator <c>v0.0.7</c> requires the field on every input, whether or not that input's
    /// ArkadeScript introspects a previous output; without it the submission is rejected with
    /// <c>missing prevout tx for input N</c>. Older emulators validate the field when present
    /// and ignore it otherwise, so attaching it is safe against any version.
    /// </para>
    /// <para>
    /// The transaction attached to Arkade input <c>i</c> is <em>not</em> the one that created
    /// that input's outpoint — that outpoint is the checkpoint, which the emulator already holds.
    /// It is the transaction funding the checkpoint's single input, i.e. the VTXO the spend
    /// actually consumes. The emulator reconciles it against the checkpoint's witness utxo.
    /// </para>
    /// <para>
    /// The field lives in the PSBT's <c>unknown</c> map, which no signature commits to, so
    /// attaching it after the wallet has signed does not invalidate anything. An input that
    /// already carries the field is left alone — the emulator rejects an input bearing two,
    /// so a caller-supplied value wins outright.
    /// </para>
    /// </remarks>
    /// <param name="arkTx">The Arkade transaction to annotate, mutated in place.</param>
    /// <param name="checkpoints">The spend's checkpoint transactions, one per Arkade input.</param>
    /// <param name="prevArkTxProvider">Resolver for the previous Arkade transactions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// A checkpoint is missing or malformed, or a previous Arkade transaction could not be
    /// resolved — either would have the emulator reject the spend, so it fails here instead
    /// with the input index and txid named.
    /// </exception>
    public static async Task AttachPrevArkTxsAsync(
        this PSBT arkTx,
        IReadOnlyList<PSBT> checkpoints,
        IPrevArkTxProvider prevArkTxProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arkTx);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(prevArkTxProvider);

        var inputs = arkTx.GetGlobalTransaction().Inputs;

        var checkpointsByTxid = new Dictionary<uint256, Transaction>();
        foreach (var checkpoint in checkpoints)
        {
            var tx = checkpoint.GetGlobalTransaction();
            checkpointsByTxid[tx.GetHash()] = tx;
        }

        // The funding txid per Arkade input, resolved through that input's checkpoint.
        var fundingTxids = new uint256[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var outpoint = inputs[i].PrevOut;
            if (!checkpointsByTxid.TryGetValue(outpoint.Hash, out var checkpoint))
            {
                throw new InvalidOperationException(
                    $"Cannot attach prevarktx for Arkade input {i}: no checkpoint spending {outpoint.Hash} " +
                    "was supplied. The emulator requires one checkpoint per Arkade input.");
            }

            if (checkpoint.Inputs.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Cannot attach prevarktx for Arkade input {i}: checkpoint {outpoint.Hash} has " +
                    $"{checkpoint.Inputs.Count} inputs, but the emulator requires exactly one.");
            }

            fundingTxids[i] = checkpoint.Inputs[0].PrevOut.Hash;
        }

        await AttachResolvedAsync(arkTx, fundingTxids, firstInput: 0, prevArkTxProvider, cancellationToken);
    }

    /// <summary>
    /// Attaches the <c>prevarktx</c> ark field to every VTXO input of an intent proof bound for
    /// the emulator's <c>POST /v1/intent</c>, resolving each previous transaction through
    /// <paramref name="prevArkTxProvider"/>.
    /// </summary>
    /// <remarks>
    /// Input 0 of a BIP322 intent proof is the message input: it carries no value and mirrors
    /// the first real input's script, and the emulator synthesises its prevout itself. Inputs
    /// 1..N are the VTXOs being registered, and each needs the transaction that created its
    /// outpoint.
    /// </remarks>
    /// <param name="intentProof">The intent proof PSBT to annotate, mutated in place.</param>
    /// <param name="prevArkTxProvider">Resolver for the previous Arkade transactions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// The proof has fewer than two inputs, or a previous Arkade transaction could not be resolved.
    /// </exception>
    public static async Task AttachIntentPrevArkTxsAsync(
        this PSBT intentProof,
        IPrevArkTxProvider prevArkTxProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intentProof);
        ArgumentNullException.ThrowIfNull(prevArkTxProvider);

        var inputs = intentProof.GetGlobalTransaction().Inputs;
        if (inputs.Count < 2)
        {
            throw new InvalidOperationException(
                "Intent proof must have at least 2 inputs (the BIP322 message input plus at least " +
                $"one VTXO), got {inputs.Count}.");
        }

        var fundingTxids = new uint256[inputs.Count];
        for (var i = 1; i < inputs.Count; i++)
            fundingTxids[i] = inputs[i].PrevOut.Hash;

        await AttachResolvedAsync(intentProof, fundingTxids, firstInput: 1, prevArkTxProvider, cancellationToken);
    }

    /// <summary>
    /// Resolves <paramref name="fundingTxids"/> in one batch and writes each result onto the
    /// matching PSBT input, from <paramref name="firstInput"/> onwards.
    /// </summary>
    private static async Task AttachResolvedAsync(
        PSBT psbt,
        uint256[] fundingTxids,
        int firstInput,
        IPrevArkTxProvider prevArkTxProvider,
        CancellationToken cancellationToken)
    {
        if (psbt.Inputs.Count != fundingTxids.Length)
        {
            throw new InvalidOperationException(
                $"Malformed PSBT: {psbt.Inputs.Count} PSBT inputs for {fundingTxids.Length} transaction inputs.");
        }

        // An input that already carries the field keeps it: a caller who attached a specific
        // previous transaction — a recursive covenant spending an Arkade transaction it just
        // built, which the indexer cannot serve yet — must win outright, and re-attaching
        // would be a wasted lookup at best.
        var pending = Enumerable.Range(firstInput, fundingTxids.Length - firstInput)
            .Where(i => psbt.Inputs[i].GetArkFieldPrevArkTx(psbt.Network) is null)
            .ToList();
        if (pending.Count == 0)
            return;

        var resolved = await prevArkTxProvider.ResolveAsync(
            [.. pending.Select(i => fundingTxids[i])], psbt.Network, cancellationToken);

        foreach (var i in pending)
        {
            if (!resolved.TryGetValue(fundingTxids[i], out var prevArkTx))
            {
                throw new InvalidOperationException(
                    $"Cannot attach prevarktx for input {i}: the previous Arkade transaction " +
                    $"{fundingTxids[i]} could not be resolved from local virtual-tx storage, the " +
                    "indexer, or chain. The emulator requires it on every input and would reject " +
                    "the submission.");
            }

            psbt.Inputs[i].SetArkFieldPrevArkTx(prevArkTx);
        }
    }

    private static IReadOnlyList<byte[]> ExtractWitnessPushes(WitScript? witScript)
    {
        if (witScript is null || witScript.PushCount == 0)
            return Array.Empty<byte[]>();
        var pushes = new byte[witScript.PushCount][];
        for (var i = 0; i < witScript.PushCount; i++)
            pushes[i] = witScript.GetUnsafePush(i);
        return pushes;
    }
}

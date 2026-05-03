using NArk.Abstractions;
using NArk.Arkade.Scripts;
using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Arkade.Introspector;

/// <summary>
/// Helpers that link the existing <see cref="ArkCoin"/> + PSBT spend flow
/// to the introspector co-signing service. The two integration points:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <description>
///     <see cref="BuildIntrospectorOutput"/> — produces the OP_RETURN
///     <c>TxOut</c> the unsigned transaction must carry so the introspector
///     can find the script bytes for each arkade-bound input. The
///     <c>TxOut</c> must be appended to the tx <em>before</em> any input is
///     signed, since signatures commit to the full output set.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="CoSignWithIntrospectorAsync"/> — submits the partially-
///     signed PSBT to the introspector and returns the PSBT with the
///     introspector's signatures added. Call after the user signer has
///     attached its own partial sigs.
///     </description>
///   </item>
/// </list>
/// <para>
/// Detection is type-driven via <see cref="IArkadeBoundScriptBuilder"/> —
/// any <see cref="ArkCoin"/> whose <c>SpendingScriptBuilder</c> implements
/// the interface is treated as arkade-bound. Spends that mix arkade and
/// non-arkade inputs are supported: only the arkade-bound inputs become
/// entries in the IntrospectorPacket.
/// </para>
/// </remarks>
public static class ArkadePsbtExtensions
{
    /// <summary>
    /// True if the spend uses at least one arkade-bound coin and therefore
    /// needs both the IntrospectorPacket OP_RETURN attachment and the
    /// post-sign introspector REST round-trip.
    /// </summary>
    public static bool RequiresIntrospectorCoSigning(IEnumerable<ArkCoin> coins)
    {
        ArgumentNullException.ThrowIfNull(coins);
        return coins.Any(c => c.SpendingScriptBuilder is IArkadeBoundScriptBuilder);
    }

    /// <summary>
    /// Build the OP_RETURN <see cref="TxOut"/> that pins each arkade-bound
    /// input's <see cref="IArkadeBoundScriptBuilder.ArkadeScript"/> + the
    /// witness pushes the script reads. Returns <c>null</c> when no input
    /// in the spend is arkade-bound (in which case the tx needs no
    /// IntrospectorPacket attached at all).
    /// </summary>
    /// <param name="coinsByVin">
    /// The spend inputs in transaction-input-index order — index <c>i</c> in
    /// this list corresponds to <c>vin = i</c> on the resulting tx.
    /// </param>
    public static TxOut? BuildIntrospectorOutput(IReadOnlyList<ArkCoin> coinsByVin)
    {
        ArgumentNullException.ThrowIfNull(coinsByVin);

        var entries = new List<IntrospectorEntry>();
        for (var vin = 0; vin < coinsByVin.Count; vin++)
        {
            if (coinsByVin[vin].SpendingScriptBuilder is not IArkadeBoundScriptBuilder arkade) continue;
            var witness = ExtractWitnessPushes(coinsByVin[vin].SpendingConditionWitness);
            entries.Add(new IntrospectorEntry((ushort)vin, arkade.ArkadeScript, witness));
        }

        if (entries.Count == 0) return null;

        var packet = new IntrospectorPacket(entries);
        var ext = new Extension([packet]);
        return ext.ToTxOut();
    }

    /// <summary>
    /// Submit a partially-signed PSBT (already carrying the user's sigs and
    /// the IntrospectorPacket OP_RETURN output) to the introspector and
    /// return the PSBT with the introspector's signatures merged in.
    /// </summary>
    /// <remarks>
    /// The introspector signs only inputs whose attached scripts pass its
    /// validation; non-arkade inputs are passed through untouched. The
    /// returned PSBT is the union of (input PSBT) + (introspector partial
    /// sigs) — assembled server-side, so this method is a thin wrapper over
    /// <see cref="IIntrospectorProvider.SubmitTxAsync"/>.
    /// </remarks>
    /// <param name="psbt">PSBT with user partial sigs already attached.</param>
    /// <param name="introspector">Provider client for the configured introspector instance.</param>
    /// <param name="checkpointTxs">Optional checkpoint PSBTs; pass an empty list when not used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<PSBT> CoSignWithIntrospectorAsync(
        this PSBT psbt,
        IIntrospectorProvider introspector,
        IReadOnlyList<string>? checkpointTxs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(psbt);
        ArgumentNullException.ThrowIfNull(introspector);

        var resp = await introspector.SubmitTxAsync(
            psbt.ToBase64(),
            checkpointTxs ?? Array.Empty<string>(),
            cancellationToken);

        // The introspector returns a PSBT that's the union of the input PSBT
        // (so user sigs are preserved) plus its own partial sigs. We can take
        // the response wholesale and parse it on the caller's network.
        return PSBT.Parse(resp.SignedArkTx, psbt.Network);
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

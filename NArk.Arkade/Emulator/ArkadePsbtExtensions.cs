using NArk.Abstractions;
using NArk.Arkade.Scripts;
using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Arkade.Emulator;

/// <summary>
/// Helpers that link the existing <see cref="ArkCoin"/> + PSBT spend flow
/// to the emulator co-signing service. The two integration points:
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
    /// The emulator signs only inputs whose attached scripts pass its
    /// validation; non-arkade inputs are passed through untouched. The
    /// returned PSBT is the union of (input PSBT) + (emulator partial
    /// sigs) — assembled server-side, so this method is a thin wrapper over
    /// <see cref="IEmulatorProvider.SubmitTxAsync"/>.
    /// </remarks>
    /// <param name="psbt">PSBT with user partial sigs already attached.</param>
    /// <param name="emulator">Provider client for the configured emulator instance.</param>
    /// <param name="checkpointTxs">Optional checkpoint PSBTs; pass an empty list when not used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<PSBT> CoSignWithEmulatorAsync(
        this PSBT psbt,
        IEmulatorProvider emulator,
        IReadOnlyList<string>? checkpointTxs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(psbt);
        ArgumentNullException.ThrowIfNull(emulator);

        var resp = await emulator.SubmitTxAsync(
            psbt.ToBase64(),
            checkpointTxs ?? Array.Empty<string>(),
            cancellationToken);

        // The emulator returns a PSBT that's the union of the input PSBT
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

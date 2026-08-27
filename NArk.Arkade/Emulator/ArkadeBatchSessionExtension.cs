using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NBitcoin;

namespace NArk.Arkade.Emulator;

/// <summary>
/// <see cref="IBatchSessionExtension"/> that drives emulator co-signing
/// at the PSBT-emitting points of a batch flow. Idempotent — passes
/// PSBTs through unchanged when no input in the batch is arkade-bound.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BatchExtensionPhase.PostTreeSigning"/> is deliberately
/// <em>not</em> implemented and throws. It routed through the emulator's
/// <c>POST /v1/tx</c> endpoint with an empty checkpoint list, which emulator
/// <c>v0.0.7</c>+ rejects outright: that endpoint requires exactly one
/// checkpoint per input, plus the <c>prevarktx</c> field on every input,
/// before it inspects anything else. Neither is available at this phase —
/// tree transactions have no checkpoints — so the hop needs a
/// checkpoint-carrying shape or an endpoint of its own. Until then, failing
/// here beats sending the emulator a submission it cannot accept.
/// </para>
/// <para>
/// <see cref="BatchExtensionPhase.PreForfeitFinalization"/> is deliberately
/// <em>not</em> implemented and throws. Forfeits are signed by the emulator's
/// dedicated <c>POST /v1/finalization</c> endpoint, which "only signs if the
/// signer's signature is found in the intent proof" and additionally requires
/// the connector tree and commitment tx. <c>POST /v1/tx</c> carries none of
/// that and does not sign forfeits, so routing this phase there would yield
/// forfeits with an incomplete witness rather than an error. Wiring it up
/// means threading the emulator-co-signed intent proof from
/// intent-registration time, which lives upstream of <c>BatchSession</c>;
/// until then, failing loudly beats silently producing unspendable forfeits.
/// </para>
/// </remarks>
public sealed class ArkadeBatchSessionExtension : IBatchSessionExtension
{
    // Retained though no phase currently submits: the co-signing client is this class's
    // reason to exist, and the submission arm that will use it lands in CoSignAsync's switch
    // once a phase the emulator can accept exists. Dropping it would break every caller's
    // construction only to have it added back.
    private readonly IEmulatorProvider _emulator;
    private readonly ILogger<ArkadeBatchSessionExtension>? _logger;

    /// <summary>Creates the extension over an emulator provider.</summary>
    /// <param name="emulator">The emulator co-signing client.</param>
    /// <param name="logger">Optional logger; co-signing is silent when omitted.</param>
    public ArkadeBatchSessionExtension(
        IEmulatorProvider emulator,
        ILogger<ArkadeBatchSessionExtension>? logger = null)
    {
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ShouldHandleAsync(IReadOnlyList<ArkCoin> spendingCoins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spendingCoins);
        var engaged = ArkadePsbtExtensions.RequiresEmulatorCoSigning(spendingCoins);
        if (engaged)
        {
            _logger?.LogInformation(
                "ArkadeBatchSessionExtension: engaging for batch with {Count} arkade-bound input(s) of {Total}",
                spendingCoins.Count(c => c.SpendingScriptBuilder is Scripts.IArkadeBoundScriptBuilder),
                spendingCoins.Count);
        }
        return Task.FromResult(engaged);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PSBT>> CoSignAsync(
        BatchExtensionPhase phase,
        IReadOnlyList<PSBT> psbts,
        IReadOnlyList<ArkCoin> spendingCoins,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(psbts);
        ArgumentNullException.ThrowIfNull(spendingCoins);

        // Defensive — BatchSession should have called ShouldHandleAsync first
        // and short-circuited, but guard against direct callers too.
        if (!ArkadePsbtExtensions.RequiresEmulatorCoSigning(spendingCoins))
        {
            _logger?.LogDebug(
                "ArkadeBatchSessionExtension: no arkade-bound inputs at {Phase}; passing {Count} PSBT(s) through",
                phase, psbts.Count);
            return psbts;
        }

        // Neither phase can be handed to the emulator as it stands, so there is deliberately
        // no submission path below this point. When one is introduced, its round-trip goes
        // in the corresponding switch arm — see the class remarks for the shape POST /v1/tx
        // requires (one checkpoint per input, prevarktx on every input, EmulatorPacket in
        // the extension output).
        throw phase switch
        {
            // Forfeits need POST /v1/finalization (signed intent proof + connector tree +
            // commitment tx), not POST /v1/tx. Sending them to /v1/tx returns a PSBT with no
            // forfeit signature, which would look like success — refuse instead.
            BatchExtensionPhase.PreForfeitFinalization => new NotSupportedException(
                "ArkadeBatchSessionExtension cannot co-sign forfeits: the emulator signs those via " +
                "POST /v1/finalization, which requires the emulator-co-signed intent proof from " +
                "intent-registration time (plus the connector tree and commitment tx). Submitting " +
                "them to POST /v1/tx would produce forfeits with an incomplete witness."),

            // Tree transactions carry no checkpoints, and POST /v1/tx requires exactly one per
            // input plus a prevarktx field on every input before it inspects anything else
            // (emulator v0.0.7, internal/application/prevout.go). Submitting anyway reaches the
            // emulator with a malformed request; refuse at the call site, where the reason is
            // legible.
            BatchExtensionPhase.PostTreeSigning => new NotSupportedException(
                "ArkadeBatchSessionExtension cannot co-sign tree transactions against emulator " +
                "v0.0.7+: POST /v1/tx requires one checkpoint per input and a prevarktx field on " +
                "every input, and a tree transaction has neither. This hop needs a " +
                "checkpoint-carrying shape or a dedicated endpoint."),

            _ => new ArgumentOutOfRangeException(nameof(phase), phase,
                "Unknown batch extension phase. A newly added phase that the emulator can accept " +
                "needs its own submission arm here; one that it cannot needs a NotSupportedException " +
                "explaining why, as the two above do."),
        };
    }
}

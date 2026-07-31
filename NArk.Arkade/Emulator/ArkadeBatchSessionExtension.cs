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
/// <see cref="BatchExtensionPhase.PostTreeSigning"/> routes through the
/// emulator's <c>POST /v1/tx</c> endpoint via
/// <see cref="ArkadePsbtExtensions.CoSignWithEmulatorAsync"/>. The
/// emulator internally decides whether to sign each input (only those
/// whose attached ArkadeScript validates against its tweaked key) and
/// returns the union of the input PSBT and its own partial sigs. Inputs
/// that aren't arkade-bound are passed through untouched on the server
/// side; non-arkade batches are short-circuited locally via
/// <see cref="ShouldHandleAsync"/>.
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

        // Forfeits need POST /v1/finalization (signed intent proof + connector tree +
        // commitment tx), not POST /v1/tx. Sending them to /v1/tx returns a PSBT with no
        // forfeit signature, which would look like success — refuse instead. See the
        // class remarks for what wiring this up requires.
        if (phase == BatchExtensionPhase.PreForfeitFinalization)
        {
            throw new NotSupportedException(
                "ArkadeBatchSessionExtension cannot co-sign forfeits: the emulator signs those via " +
                "POST /v1/finalization, which requires the emulator-co-signed intent proof from " +
                "intent-registration time (plus the connector tree and commitment tx). Submitting " +
                "them to POST /v1/tx would produce forfeits with an incomplete witness.");
        }

        _logger?.LogInformation(
            "ArkadeBatchSessionExtension: co-signing {Count} PSBT(s) at {Phase}",
            psbts.Count, phase);

        var signed = new PSBT[psbts.Count];
        for (var i = 0; i < psbts.Count; i++)
        {
            try
            {
                signed[i] = await psbts[i].CoSignWithEmulatorAsync(_emulator, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex,
                    "ArkadeBatchSessionExtension: emulator rejected PSBT {Index}/{Count} at {Phase}",
                    i, psbts.Count, phase);
                throw;
            }
        }
        return signed;
    }
}

using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NBitcoin;

namespace NArk.Arkade.Introspector;

/// <summary>
/// <see cref="IBatchSessionExtension"/> that drives introspector co-signing
/// at the two PSBT-emitting points of a batch flow. Idempotent — passes
/// PSBTs through unchanged when no input in the batch is arkade-bound.
/// </summary>
/// <remarks>
/// <para>
/// Both phases route through the introspector's <c>POST /v1/tx</c> endpoint
/// via <see cref="ArkadePsbtExtensions.CoSignWithIntrospectorAsync"/>. The
/// introspector internally decides whether to sign each input (only those
/// whose attached ArkadeScript validates against its tweaked key) and
/// returns the union of the input PSBT and its own partial sigs. Inputs
/// that aren't arkade-bound are passed through untouched on the server
/// side; non-arkade batches are short-circuited locally via
/// <see cref="ShouldHandleAsync"/>.
/// </para>
/// <para>
/// The dedicated <c>POST /v1/finalization</c> endpoint (which carries a
/// signed-intent envelope alongside forfeits and commitment tx) is not
/// used here — that path requires threading the introspector-co-signed
/// intent proof from intent-registration time, which lives upstream of
/// <c>BatchSession</c>. Once that wire-up exists, this extension can
/// switch <c>PreForfeitFinalization</c> to call <c>SubmitFinalizationAsync</c>
/// instead.
/// </para>
/// </remarks>
public sealed class ArkadeBatchSessionExtension : IBatchSessionExtension
{
    private readonly IIntrospectorProvider _introspector;
    private readonly ILogger<ArkadeBatchSessionExtension>? _logger;

    public ArkadeBatchSessionExtension(
        IIntrospectorProvider introspector,
        ILogger<ArkadeBatchSessionExtension>? logger = null)
    {
        _introspector = introspector ?? throw new ArgumentNullException(nameof(introspector));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ShouldHandleAsync(IReadOnlyList<ArkCoin> spendingCoins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spendingCoins);
        var engaged = ArkadePsbtExtensions.RequiresIntrospectorCoSigning(spendingCoins);
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
        if (!ArkadePsbtExtensions.RequiresIntrospectorCoSigning(spendingCoins))
        {
            _logger?.LogDebug(
                "ArkadeBatchSessionExtension: no arkade-bound inputs at {Phase}; passing {Count} PSBT(s) through",
                phase, psbts.Count);
            return psbts;
        }

        _logger?.LogInformation(
            "ArkadeBatchSessionExtension: co-signing {Count} PSBT(s) at {Phase}",
            psbts.Count, phase);

        var signed = new PSBT[psbts.Count];
        for (var i = 0; i < psbts.Count; i++)
        {
            try
            {
                signed[i] = await psbts[i].CoSignWithIntrospectorAsync(
                    _introspector, checkpointTxs: null, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex,
                    "ArkadeBatchSessionExtension: introspector rejected PSBT {Index}/{Count} at {Phase}",
                    i, psbts.Count, phase);
                throw;
            }
        }
        return signed;
    }
}

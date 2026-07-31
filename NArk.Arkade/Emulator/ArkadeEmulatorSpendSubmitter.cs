using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Core.Helpers;
using NBitcoin;

namespace NArk.Arkade.Emulator;

/// <summary>
/// <see cref="ISpendSubmitHandler"/> that routes covenant (arkade-bound) offchain
/// spends through the emulator co-signing service instead of arkd directly. The
/// emulator validates each input's ArkadeScript, adds its co-signature, forwards the
/// set to arkd and finalizes — so once it returns, the spend is fully submitted.
/// </summary>
/// <remarks>
/// Engages only when at least one input is arkade-bound
/// (<see cref="ArkadePsbtExtensions.RequiresEmulatorCoSigning"/>); every other spend
/// falls through to the unchanged arkd cooperative flow. The Arkade transaction and checkpoints
/// arrive already user-signed — this handler only adds the emulator round-trip.
/// </remarks>
public sealed class ArkadeEmulatorSpendSubmitter(
    IEmulatorProvider emulator,
    ILogger<ArkadeEmulatorSpendSubmitter>? logger = null) : ISpendSubmitHandler
{
    /// <inheritdoc />
    public bool ShouldHandle(IReadOnlyCollection<ArkCoin> coins)
    {
        ArgumentNullException.ThrowIfNull(coins);
        return ArkadePsbtExtensions.RequiresEmulatorCoSigning(coins);
    }

    /// <inheritdoc />
    public async Task SubmitAsync(
        IReadOnlyCollection<ArkCoin> coins,
        PSBT arkTx,
        IReadOnlyList<PSBT> checkpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arkTx);
        ArgumentNullException.ThrowIfNull(checkpoints);

        logger?.LogInformation(
            "ArkadeEmulatorSpendSubmitter: submitting covenant spend with {Count} checkpoint(s) to the emulator",
            checkpoints.Count);

        var response = await emulator.SubmitTxAsync(
            arkTx.ToBase64(),
            [.. checkpoints.Select(c => c.ToBase64())],
            cancellationToken);

        // POST /v1/tx forwards to arkd and finalizes only when this emulator is the last
        // required non-arkd signer; otherwise it returns just its own signatures and the
        // spend was never submitted. Discarding the response makes those two outcomes
        // indistinguishable, so assert the shape this handler's contract depends on
        // instead of assuming it.
        if (string.IsNullOrEmpty(response.SignedArkTx))
        {
            throw new InvalidOperationException(
                "Emulator returned no signed Arkade transaction for a covenant spend — the spend was not submitted.");
        }

        if (checkpoints.Count > 0 && response.SignedCheckpointTxs.Count != checkpoints.Count)
        {
            throw new InvalidOperationException(
                $"Emulator returned {response.SignedCheckpointTxs.Count} signed checkpoint(s) for " +
                $"{checkpoints.Count} submitted — the covenant spend was not fully co-signed.");
        }

        logger?.LogDebug(
            "ArkadeEmulatorSpendSubmitter: emulator returned a signed Arkade transaction and {Count} signed checkpoint(s)",
            response.SignedCheckpointTxs.Count);
    }
}

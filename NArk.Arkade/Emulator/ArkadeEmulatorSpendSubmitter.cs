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
/// falls through to the unchanged arkd cooperative flow. The ark tx and checkpoints
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

        await emulator.SubmitTxAsync(
            arkTx.ToBase64(),
            [.. checkpoints.Select(c => c.ToBase64())],
            cancellationToken);
    }
}

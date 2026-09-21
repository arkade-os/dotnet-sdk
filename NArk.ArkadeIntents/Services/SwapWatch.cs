using NArk.Abstractions.Contracts;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NBitcoin;

namespace NArk.ArkadeIntents.Services;

/// <summary>Starts or stops watching the scripts a receive swap left behind: its lockup and its payout.</summary>
internal static class SwapWatch
{
    public static bool IsClosedByClock(ArkadeSwapIntent intent) =>
        intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.ClosedByClockAt);

    public static async Task CloseAsync(
        IContractStorage? contracts, ArkadeSwapIntent intent, ArkadeSwapIntentStatus status, long now,
        Network network, CancellationToken cancellationToken)
    {
        intent.Status = status;
        intent.Metadata[ArkadeSwapMetadataKeys.ClosedByClockAt] = now.ToString();
        await SetAsync(contracts, intent, ContractActivityState.Inactive, network, cancellationToken);
    }

    public static async Task ReopenAsync(
        IContractStorage? contracts, ArkadeSwapIntent intent, Network network, CancellationToken cancellationToken)
    {
        intent.Status = ArkadeSwapIntentStatus.Pending;
        intent.Metadata.Remove(ArkadeSwapMetadataKeys.ClosedByClockAt);
        await SetAsync(contracts, intent, ContractActivityState.AwaitingFundsBeforeDeactivate, network, cancellationToken);
    }

    private static async Task SetAsync(
        IContractStorage? contracts, ArkadeSwapIntent intent, ContractActivityState state, Network network,
        CancellationToken cancellationToken)
    {
        if (contracts is null) return;

        await contracts.UpdateContractActivityState(intent.WalletId, intent.SwapPkScript, state, cancellationToken);

        // A lockup that no longer loads has no payout to read; the lockup alone is still worth toggling.
        string? payout;
        try
        {
            var lockup = await LightningCorridor.LoadLockupAsync(
                contracts, intent.SwapPkScript, intent.Id, network, cancellationToken);
            payout = lockup.NonInteractiveClaim?.ReceiverPkScript is { } pk ? Convert.ToHexString(pk).ToLowerInvariant() : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            payout = null;
        }

        if (payout is not null)
            await contracts.UpdateContractActivityState(intent.WalletId, payout, state, cancellationToken);
    }
}

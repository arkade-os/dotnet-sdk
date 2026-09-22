using NArk.Abstractions.Contracts;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NBitcoin;

namespace NArk.ArkadeIntents.Services;

/// <summary>Closes or reopens a swap that ended with no chain event, and (un)watches its lockup and payout.</summary>
internal static class SwapWatch
{
    public static bool IsClosedWithoutChainEvent(ArkadeSwapIntent intent) =>
        intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.ClosedWithoutChainEventAt);

    public static async Task CloseAsync(
        IContractStorage? contracts, ArkadeSwapIntent intent, ArkadeSwapIntentStatus status, long now,
        Network network, CancellationToken cancellationToken)
    {
        intent.Status = status;
        intent.Metadata[ArkadeSwapMetadataKeys.ClosedWithoutChainEventAt] = now.ToString();
        await SetAsync(contracts, intent, ContractActivityState.Inactive, network, cancellationToken);
    }

    public static async Task ReopenAsync(
        IContractStorage? contracts, ArkadeSwapIntent intent, Network network, CancellationToken cancellationToken)
    {
        // A send goes back to Funding, so the advance pass re-reads whether its lockup landed.
        intent.Status = intent.Type == ArkadeSwapIntentType.BtcToLightning
            ? ArkadeSwapIntentStatus.Funding
            : ArkadeSwapIntentStatus.Pending;
        intent.Metadata.Remove(ArkadeSwapMetadataKeys.ClosedWithoutChainEventAt);
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

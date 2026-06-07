using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NBitcoin;

namespace NArk.Swaps.Extensions;

public static class SwapExtensions
{
    /// <summary>
    /// Returns the cached refund destination from swap metadata, or derives a fresh contract
    /// and caches it. Subsequent retries reuse the same contract instead of leaking new rows
    /// into <see cref="IContractStorage"/> on every poll tick.
    /// </summary>
    /// <returns>The destination to refund to, and the (possibly updated) swap record.</returns>
    public static async Task<(IDestination Destination, ArkSwap Swap)> GetOrDeriveRefundDestinationAsync(
        this ArkSwap swap,
        IContractService contractService,
        ISwapStorage swapStorage,
        Network network,
        CancellationToken ct)
    {
        if (swap.Get(SwapMetadata.RefundDestination) is { } cached)
            return (ArkAddress.Parse(cached), swap);

        var contract = await contractService.DeriveContract(
            swap.WalletId,
            NextContractPurpose.SendToSelf,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"swap-refund:{swap.SwapId}" },
            cancellationToken: ct)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: failed to derive refund destination");

        var addr = contract.GetArkAddress();
        var updated = swap with
        {
            Metadata = new Dictionary<string, string>(swap.Metadata ?? [])
                { [SwapMetadata.RefundDestination] = addr.ToString(network == Network.Main) },
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await swapStorage.SaveSwap(swap.WalletId, updated, ct);
        return (addr, updated);
    }

    public static bool IsActive(this ArkSwapStatus swapStatus)
    {
        return swapStatus is ArkSwapStatus.Pending or ArkSwapStatus.Unknown;
    }

    public static string? Get(this ArkSwap swap, string key)
    {
        return swap.Metadata?.TryGetValue(key, out var value) == true ? value : null;
    }

    public static bool IsTerminalState(this ArkSwapStatus status)
    {
        return status is ArkSwapStatus.Refunded or ArkSwapStatus.Settled or ArkSwapStatus.Failed;
    }

    public static bool IsSuccess(this ArkSwapStatus status)
    {
        return status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded;
    }
}
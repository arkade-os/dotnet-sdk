using NArk.Abstractions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;

namespace NArk.Swaps.Evm.Extensions;

/// <summary>
/// EVM-leg convenience methods for <see cref="SwapsManagementService"/>, mirroring the shape of
/// its own <c>InitiateBtcToArkChainSwap</c>/<c>InitiateArkToBtcChainSwap</c> — as extension
/// methods rather than members on that class, since <c>NArk.Swaps</c> must never depend on
/// <c>NArk.Swaps.Evm</c> (this assembly already depends on <c>NArk.Swaps</c>, so the reverse
/// direction here is fine). Built on the three passthroughs
/// <see cref="SwapsManagementService.WalletProvider"/>/<see cref="SwapsManagementService.SpendingService"/>/
/// <see cref="SwapsManagementService.SwapStorage"/> expose for exactly this purpose.
/// </summary>
public static class SwapsManagementServiceEvmExtensions
{
    /// <summary>
    /// Initiates an ARK -&gt; EvmArbitrum chain swap: derives a refund descriptor, creates the
    /// swap (Boltz will lock tBTC for this provider's own <see cref="EvmChainSwapProvider.EvmAddress"/>
    /// to claim), then funds the Ark VHTLC lockup. Mirrors
    /// <see cref="SwapsManagementService.InitiateArkToBtcChainSwap"/>'s exact
    /// fund-then-mark-Failed-on-exception pattern. Returns the swap id.
    /// </summary>
    public static async Task<string> InitiateArkToEvmChainSwap(
        this SwapsManagementService mgmt, string walletId, long amountSats, CancellationToken ct = default)
    {
        var evm = mgmt.Providers.OfType<EvmChainSwapProvider>().Single();

        var addressProvider = await mgmt.WalletProvider.GetAddressProviderAsync(walletId, ct);
        var refundDescriptor = await addressProvider!.GetNextSigningDescriptor(ct);

        var result = await evm.CreateArkToEvmSwapAsync(
            walletId, amountSats, refundDescriptor, evm.EvmAddress, ct: ct);

        var swap = (await mgmt.SwapStorage.GetSwaps(swapIds: [result.Swap.Id], cancellationToken: ct)).Single();
        try
        {
            await mgmt.SpendingService.Spend(walletId,
                [new ArkTxOut(ArkTxOutType.Vtxo, amountSats, result.Contract!.GetArkAddress())], ct);
        }
        catch (Exception e)
        {
            await mgmt.SwapStorage.SaveSwap(walletId,
                swap with { Status = ArkSwapStatus.Failed, FailReason = e.ToString(), UpdatedAt = DateTimeOffset.UtcNow },
                ct);
            throw;
        }

        return result.Swap.Id;
    }

    /// <summary>
    /// Initiates an EvmArbitrum -&gt; ARK chain swap: derives a claim descriptor, creates the
    /// swap, then locks tBTC in <c>ERC20Swap</c> ourselves. Unlike
    /// <see cref="SwapsManagementService.InitiateBtcToArkChainSwap"/> (which returns an address
    /// for an external BTC wallet to pay), this provider already holds the EVM signing key
    /// (<see cref="EvmSwapOptions.PrivateKey"/>) needed to lock — so this completes the lock
    /// itself rather than handing lock parameters back to the caller. Returns the swap id.
    /// </summary>
    public static async Task<string> InitiateEvmToArkChainSwap(
        this SwapsManagementService mgmt, string walletId, long amountSats, CancellationToken ct = default)
    {
        var evm = mgmt.Providers.OfType<EvmChainSwapProvider>().Single();

        var addressProvider = await mgmt.WalletProvider.GetAddressProviderAsync(walletId, ct);
        var claimDescriptor = await addressProvider!.GetNextSigningDescriptor(ct);

        var result = await evm.CreateEvmToArkSwapAsync(walletId, amountSats, claimDescriptor, ct: ct);

        try
        {
            await evm.LockEvmAsync(result, ct);
        }
        catch (Exception e)
        {
            var swap = (await mgmt.SwapStorage.GetSwaps(swapIds: [result.Swap.Id], cancellationToken: ct)).Single();
            await mgmt.SwapStorage.SaveSwap(walletId,
                swap with { Status = ArkSwapStatus.Failed, FailReason = e.ToString(), UpdatedAt = DateTimeOffset.UtcNow },
                ct);
            throw;
        }

        return result.Swap.Id;
    }
}

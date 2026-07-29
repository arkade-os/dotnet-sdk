using System.Numerics;
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
        // TODO: .Single() assumes exactly one EvmChainSwapProvider is registered (one EVM chain,
        // Arbitrum). Once a second EVM chain is supported, this needs a chain-id/route-aware
        // lookup instead of grabbing whichever single instance is registered.
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
        // TODO: .Single() assumes exactly one EvmChainSwapProvider is registered (one EVM chain,
        // Arbitrum). Once a second EVM chain is supported, this needs a chain-id/route-aware
        // lookup instead of grabbing whichever single instance is registered.
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
            await MarkFailedIfNothingLockedAsync(mgmt, evm, walletId, result, e, ct);
            throw;
        }

        return result.Swap.Id;
    }

    /// <summary>
    /// Marks the swap <see cref="ArkSwapStatus.Failed"/> only when the EVM leg provably holds
    /// nothing — otherwise leaves it active for the poll loop.
    /// </summary>
    /// <remarks>
    /// A throw out of the lock call does not mean the funds are safe. The broadcast and the
    /// receipt wait are separate steps, so an RPC timeout, a dropped connection or a process
    /// restart all surface as an exception <em>after</em> the tokens are already committed in
    /// <c>ERC20Swap</c>. Marking such a swap Failed drops it out of
    /// <c>RunPollLoopAsync</c>'s active set, and the poll loop is the only thing that would
    /// ever refund it — so the funds would sit locked until the timelock expires with nobody
    /// watching. When in doubt, stay active: a Pending swap that turns out to hold nothing
    /// costs a few wasted poll ticks and gets marked Failed by
    /// <c>TryRefundEvmLockupAsync</c> on expiry anyway.
    /// </remarks>
    private static async Task MarkFailedIfNothingLockedAsync(
        SwapsManagementService mgmt, EvmChainSwapProvider evm, string walletId,
        EvmChainSwapResult result, Exception cause, CancellationToken ct)
    {
        if (await evm.HasCommittedEvmLockupAsync(result, ct))
            return;

        var swap = (await mgmt.SwapStorage.GetSwaps(swapIds: [result.Swap.Id], cancellationToken: ct)).Single();
        await mgmt.SwapStorage.SaveSwap(walletId,
            swap with { Status = ArkSwapStatus.Failed, FailReason = cause.ToString(), UpdatedAt = DateTimeOffset.UtcNow },
            ct);
    }

    /// <summary>
    /// Milestone 4 alternative to <see cref="InitiateEvmToArkChainSwap"/>: funds the same
    /// EvmArbitrum -&gt; ARK chain swap from an arbitrary ERC20 (e.g. USDT) instead of tBTC
    /// directly, via <see cref="EvmChainSwapProvider.LockEvmFromErc20Async"/> — requires the
    /// registered <see cref="EvmChainSwapProvider"/> to have been constructed with a
    /// <c>DEXSwapService</c> (see that method's TODO — no production DEX-quoting implementation
    /// exists yet, so this is only usable with a caller-supplied test/mock
    /// <c>IDexQuoteProvider</c> today). Returns the swap id.
    /// </summary>
    public static async Task<string> InitiateEvmToArkChainSwapFromErc20(
        this SwapsManagementService mgmt, string walletId, long amountSats, string tokenInAddress,
        BigInteger amountIn, CancellationToken ct = default)
    {
        var evm = mgmt.Providers.OfType<EvmChainSwapProvider>().Single();

        var addressProvider = await mgmt.WalletProvider.GetAddressProviderAsync(walletId, ct);
        var claimDescriptor = await addressProvider!.GetNextSigningDescriptor(ct);

        var result = await evm.CreateEvmToArkSwapAsync(walletId, amountSats, claimDescriptor, ct: ct);

        try
        {
            await evm.LockEvmFromErc20Async(result, tokenInAddress, amountIn, ct);
        }
        catch (Exception e)
        {
            // Same reasoning as the plain-tBTC path — the DEX-hop lock is one atomic transaction,
            // so a lost receipt leaves tokens committed in ERC20Swap just the same.
            await MarkFailedIfNothingLockedAsync(mgmt, evm, walletId, result, e, ct);
            throw;
        }

        return result.Swap.Id;
    }
}

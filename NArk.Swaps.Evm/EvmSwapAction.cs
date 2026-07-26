namespace NArk.Swaps.Evm;

/// <summary>
/// Actions the EVM chain-swap poll loop can take, mirroring the shape of
/// <c>NArk.Swaps.Boltz.BoltzSwapAction</c> — but self-contained here rather than added to
/// that enum, so <c>NArk.Swaps</c> never needs to change (or know about EVM) for this
/// provider to exist. Non-cooperative milestone only: no MuSig2/EIP-712 equivalents yet.
/// </summary>
public enum EvmSwapAction
{
    /// <summary>ChainArkToEvm: Boltz locked tBTC for us — claim it on Arbitrum with our preimage.</summary>
    CanClaimEvmLockup,

    /// <summary>ChainEvmToArk: our tBTC lockup timed out — refund it ourselves on Arbitrum.</summary>
    CanRefundEvmLockup,

    /// <summary>
    /// ChainArkToEvm: swap expired before Boltz locked tBTC — refund our Ark lockup.
    /// Implemented in <see cref="EvmChainSwapProvider.TryCoopRefundArkToEvm"/>: cooperative
    /// refund first, falling back to the refund-without-receiver batch-intent path, mirroring
    /// <c>BoltzSwapProvider.Refunds.cs</c>'s <c>ChainArkToBtc</c> path.
    /// </summary>
    CanRefundArkLockup,

    /// <summary>
    /// ChainEvmToArk: Boltz locked Ark VTXOs for us — claimed automatically by the wallet-wide
    /// generic sweeper (<c>SweeperService</c> + <c>SwapSweepPolicy</c> +
    /// <c>VHTLCContractTransformer</c>), not by this provider. Matches the same reliance
    /// <c>BoltzSwapProvider</c>'s <c>ChainBtcToArk</c> direction already has (see
    /// <c>SwapsManagementService.InitiateBtcToArkChainSwap</c>'s "Import VHTLC contract for
    /// sweeper to claim" comment) — no explicit action needed here.
    /// </summary>
    CanClaimArkLockup,
}

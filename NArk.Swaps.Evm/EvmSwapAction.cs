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
    /// Not yet implemented: requires the same Ark VHTLC refund-intent machinery
    /// <c>BoltzSwapProvider.Refunds.cs</c> uses for the BTC leg (PSBT co-sign /
    /// refund-without-receiver batch intent). Deferred — see plan's follow-up scope.
    /// </summary>
    CanRefundArkLockup,

    /// <summary>
    /// ChainEvmToArk: Boltz locked Ark VTXOs for us — claim them with our preimage.
    /// Not yet implemented: requires generic Ark VHTLC spending via
    /// <c>SpendingService</c>. Deferred — see plan's follow-up scope.
    /// </summary>
    CanClaimArkLockup,
}

using NArk.Swaps.Boltz;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;

namespace NArk.Swaps.Evm;

/// <summary>
/// Pure <c>(ArkSwapType, BoltzStatus) -&gt; EvmSwapAction</c> classifier for the EVM chain-swap
/// legs, mirroring the shape of <c>NArk.Swaps.Boltz.BoltzOperationClassifier</c>. Reuses the
/// shared <see cref="BoltzSwapStatus"/> string vocabulary (Boltz's status strings are
/// swap-type-agnostic) but is otherwise self-contained — see <see cref="EvmSwapAction"/> for
/// why this isn't just added to the existing Boltz classifier.
/// </summary>
public static class EvmChainOperationClassifier
{
    public static EvmSwapAction? Classify(ArkSwap swap, string boltzStatus)
    {
        if (CanClaimEvmLockup(swap, boltzStatus))
            return EvmSwapAction.CanClaimEvmLockup;

        if (CanRefundEvmLockup(swap, boltzStatus))
            return EvmSwapAction.CanRefundEvmLockup;

        if (CanRefundArkLockup(swap, boltzStatus))
            return EvmSwapAction.CanRefundArkLockup;

        if (CanClaimArkLockup(swap, boltzStatus))
            return EvmSwapAction.CanClaimArkLockup;

        return null;
    }

    public static bool CanClaimEvmLockup(ArkSwap swap, string status) =>
        ValidateTypeAndStatus(swap, ArkSwapType.ChainArkToEvm) &&
        status is BoltzSwapStatus.TransactionServerMempool or BoltzSwapStatus.TransactionServerConfirmed;

    public static bool CanRefundArkLockup(ArkSwap swap, string status) =>
        ValidateTypeAndStatus(swap, ArkSwapType.ChainArkToEvm) && status == BoltzSwapStatus.SwapExpired;

    public static bool CanRefundEvmLockup(ArkSwap swap, string status) =>
        ValidateTypeAndStatus(swap, ArkSwapType.ChainEvmToArk) && status == BoltzSwapStatus.SwapExpired;

    public static bool CanClaimArkLockup(ArkSwap swap, string status) =>
        ValidateTypeAndStatus(swap, ArkSwapType.ChainEvmToArk) &&
        status is BoltzSwapStatus.TransactionServerMempool or BoltzSwapStatus.TransactionServerConfirmed;

    private static bool ValidateTypeAndStatus(ArkSwap swap, ArkSwapType expectedType) =>
        swap.SwapType == expectedType && !swap.Status.IsSuccess();
}

using System.Diagnostics.CodeAnalysis;
using NArk.Swaps.Models;

namespace NArk.Swaps.Boltz;

public class BoltzOperationClassifier
{
    
    public static BoltzSwapAction? Classify(ArkSwap swap, string boltzStatus)
    {
        if (CanRenegotiateChainSwap(swap, boltzStatus))
        {
            return BoltzSwapAction.CanRenegotiateChain;
        }

        if (CanCoopRefundSubmarine(swap, boltzStatus))
        {
            return BoltzSwapAction.CanCoopRefundSubmarine;
        }

        if (CanCoopRefundChainSwap(swap, boltzStatus))
        {
            return BoltzSwapAction.CanCoopRefundChain;
        }

        if (CanClaimChainSwap(swap, boltzStatus))
        {
            return BoltzSwapAction.CanClaimChain;
        }

        if (ShouldSignChainSwap(swap, boltzStatus))
        {
            return BoltzSwapAction.ReadyToSignClaim;
        }

        return null;
    }
    
    
    //Renegotiation allows Chain Swaps that failed due to an incorrect lockup amount
    //to be salvaged instead of requiring a refund.
    //It applies only to Chain Swaps, not Submarine or Reverse Swaps.
    public static bool CanRenegotiateChainSwap(ArkSwap swap, string chainSwapStatus)
    {
        if (!IsChainSwap(swap))
        {
            return false;
        }

        if (swap.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded)
        {
            return false;
        }
        return chainSwapStatus == BoltzSwapStatus.TransactionLockupFailed;
    }

    // if (swap.SwapType is ArkSwapType.Submarine && swap.Status is not ArkSwapStatus.Refunded &&
    // IsRefundableStatus(swapStatus.Status))
    public static bool CanCoopRefundSubmarine(ArkSwap swap, string boltzSwapStatus)
    {
        if (swap.SwapType != ArkSwapType.Submarine)
        {
            return false;
        }

        return swap.Status != ArkSwapStatus.Refunded && IsRefundableStatus(boltzSwapStatus);
    }

    //if ((swap.SwapType is ArkSwapType.ChainBtcToArk or ArkSwapType.ChainArkToBtc) &&
    // (swap.Status is not (ArkSwapStatus.Settled or ArkSwapStatus.Refunded)) &&
    // IsChainRefundableStatus(swapStatus.Status))
    public static bool CanCoopRefundChainSwap(ArkSwap swap, string boltzSwapStatus)
    {
        if (!IsChainSwap(swap))
        {
            return false;
        }

        if (swap.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded)
        {
            return false;
        }

        return boltzSwapStatus == BoltzSwapStatus.SwapExpired;
    }
    
    public static bool CanClaimChainSwap(ArkSwap swap, string status)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToBtc)
        {
            return false; 
        }

        return status is BoltzSwapStatus.TransactionServerMempool or BoltzSwapStatus.TransactionServerConfirmed;
    }


    public static bool ShouldSignChainSwap(ArkSwap swap, string status)
    {
        if (swap.SwapType != ArkSwapType.ChainBtcToArk)
        {
            return false;
        }

        return status is BoltzSwapStatus.TransactionClaimPending;
    }
    
    
    public enum BoltzSwapAction
    {
        CanCoopRefundSubmarine,
        CanCoopRefundChain,
        CanRenegotiateChain,
        CanClaimChain,
        ReadyToSignClaim,
    }
    
    internal static bool IsRefundableStatus(string status)
    {
        return status switch
        {
            BoltzSwapStatus.InvoiceFailedToPay => true,
            BoltzSwapStatus.InvoiceExpired => true,
            BoltzSwapStatus.SwapExpired => true,
            BoltzSwapStatus.TransactionLockupFailed => true,
            _ => false
        };
    }
    internal static bool IsChainSwap(ArkSwap swap) =>
        swap.SwapType is ArkSwapType.ChainArkToBtc or ArkSwapType.ChainBtcToArk;
}
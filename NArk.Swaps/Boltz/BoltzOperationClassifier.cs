using System.Diagnostics.CodeAnalysis;
using NArk.Swaps.Extensions;
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

        if (CanCoopRefundArkToBtc(swap, boltzStatus))
        {
            return BoltzSwapAction.CanCoopRefundArkToBtc;
        }
        
        if (CanCoopRefundBtcToArk(swap, boltzStatus))
        {
            return BoltzSwapAction.CanCoopRefundBtcToArk;
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

        if (swap.Status.IsSuccess())
        {
            return false;
        }
        return chainSwapStatus == BoltzSwapStatus.TransactionLockupFailed;
    }
    
    public static bool CanCoopRefundSubmarine(ArkSwap swap, string boltzSwapStatus) =>
        IsCoopRefundable(swap, ArkSwapType.Submarine) && IsRefundableStatus(boltzSwapStatus);

    public static bool CanCoopRefundArkToBtc(ArkSwap swap, string boltzSwapStatus) => 
        IsCoopRefundable(swap, ArkSwapType.ChainArkToBtc) && boltzSwapStatus == BoltzSwapStatus.SwapExpired;
    
    public static bool CanCoopRefundBtcToArk(ArkSwap swap, string boltzSwapStatus) =>
        IsCoopRefundable(swap, ArkSwapType.ChainBtcToArk) && boltzSwapStatus == BoltzSwapStatus.SwapExpired;
    
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
        CanCoopRefundArkToBtc,
        CanCoopRefundBtcToArk,
        CanRenegotiateChain,
        CanClaimChain,
        ReadyToSignClaim,
    }
    
    private static bool IsRefundableStatus(string status)
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
    private static bool IsChainSwap(ArkSwap swap) =>
        swap.SwapType is ArkSwapType.ChainArkToBtc or ArkSwapType.ChainBtcToArk;
    private static bool IsCoopRefundable(ArkSwap swap, ArkSwapType expectedType)
    {
        if (swap.SwapType != expectedType)
        {
            return false;
        }
        return !swap.Status.IsSuccess();
    }

}
using NArk.Swaps.Models;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Boltz swap status strings as defined at https://api.docs.boltz.exchange/lifecycle.html.
/// Not every status applies to every swap type — see the inline comments.
/// </summary>
public static class BoltzSwapStatus
{
    // ── Common ────────────────────────────────────────────────────────────────

    /// <summary>Swap registered, waiting for user action. All swap types.</summary>
    public const string SwapCreated = "swap.created";

    /// <summary>Swap timed out with no lockup observed. All swap types.</summary>
    public const string SwapExpired = "swap.expired";

    // ── On-chain lockup ───────────────────────────────────────────────────────

    /// <summary>User's lockup transaction seen in mempool. Submarine and chain swaps.</summary>
    public const string TransactionMempool = "transaction.mempool";

    /// <summary>User's lockup transaction confirmed on-chain. Submarine and chain swaps.</summary>
    public const string TransactionConfirmed = "transaction.confirmed";

    /// <summary>Boltz's server-side lockup seen in mempool. Chain swaps and reverse swaps.</summary>
    public const string TransactionServerMempool = "transaction.server.mempool";

    /// <summary>Boltz's server-side lockup confirmed on-chain. Chain swaps and reverse swaps.</summary>
    public const string TransactionServerConfirmed = "transaction.server.confirmed";

    /// <summary>
    /// Funded amount doesn't match the quote; user must renegotiate via the quote endpoint
    /// or wait for Boltz to refund. Submarine and chain swaps.
    /// </summary>
    public const string TransactionLockupFailed = "transaction.lockupFailed";

    /// <summary>
    /// Boltz's server-side transaction failed (e.g. fee too low). Reverse and chain swaps.
    /// Cooperative refund path applies.
    /// </summary>
    public const string TransactionFailed = "transaction.failed";

    // ── Claim ─────────────────────────────────────────────────────────────────

    /// <summary>Cooperative claim pending Boltz's counter-signature. Submarine and chain swaps.</summary>
    public const string TransactionClaimPending = "transaction.claim.pending";

    /// <summary>Claim transaction confirmed on-chain. Terminal. All swap types.</summary>
    public const string TransactionClaimed = "transaction.claimed";

    // ── Refund ────────────────────────────────────────────────────────────────

    /// <summary>Refund transaction confirmed on-chain. Terminal. Reverse and chain swaps.</summary>
    public const string TransactionRefunded = "transaction.refunded";

    // ── Invoice (submarine swaps) ─────────────────────────────────────────────

    /// <summary>Boltz has received and validated the Lightning invoice. Submarine swaps.</summary>
    public const string InvoiceSet = "invoice.set";

    /// <summary>Boltz is attempting to pay the Lightning invoice. Submarine swaps.</summary>
    public const string InvoicePending = "invoice.pending";

    /// <summary>Lightning invoice paid successfully. Submarine swaps.</summary>
    public const string InvoicePaid = "invoice.paid";

    /// <summary>Boltz failed to pay the Lightning invoice; cooperative refund applies. Submarine swaps.</summary>
    public const string InvoiceFailedToPay = "invoice.failedToPay";

    // ── Invoice (reverse submarine swaps) ────────────────────────────────────

    /// <summary>Lightning invoice settled (preimage revealed). Terminal. Reverse swaps.</summary>
    public const string InvoiceSettled = "invoice.settled";

    /// <summary>Lightning invoice expired before payment. Reverse swaps.</summary>
    public const string InvoiceExpired = "invoice.expired";

    // ── Miner fee (reverse submarine swaps) ──────────────────────────────────

    /// <summary>Prepaid miner fee received. Reverse swaps only.</summary>
    public const string MinerFeePaid = "minerfee.paid";


    public static ArkSwapStatus ToArkSwapStatus(string status)
    {
        return status switch
        {
            SwapCreated or InvoiceSet => ArkSwapStatus.Pending, 
            
            InvoiceFailedToPay or InvoiceExpired or 
                SwapExpired or TransactionFailed or 
                TransactionRefunded => ArkSwapStatus.Failed, 
            
                TransactionMempool or TransactionConfirmed => ArkSwapStatus.Pending, 
                InvoiceSettled or TransactionClaimed => ArkSwapStatus.Settled, 
            
                // Chain swap specific statuses 
                TransactionServerMempool or TransactionServerConfirmed or TransactionClaimPending => ArkSwapStatus.Pending, 
                TransactionLockupFailed => ArkSwapStatus.Failed, 
            
                _ => ArkSwapStatus.Unknown
        
        };
    }
}
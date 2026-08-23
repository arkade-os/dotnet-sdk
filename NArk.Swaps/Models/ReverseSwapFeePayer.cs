namespace NArk.Swaps.Models;

/// <summary>
/// Determines who absorbs the Boltz reverse-swap service fee.
/// </summary>
public enum ReverseSwapFeePayer
{
    /// <summary>
    /// The receiver absorbs the fee. The Lightning invoice is for <em>exactly</em> the requested
    /// amount (Boltz <c>invoiceAmount</c>), so payer wallets that verify the invoice against the
    /// amount they chose to pay (LNURL-pay / LUD-06) accept it. The receiver nets
    /// <c>requested − fee</c> on-chain. This is the default and the only LUD-06-compliant option.
    /// </summary>
    Recipient,

    /// <summary>
    /// The sender absorbs the fee. The receiver gets <em>exactly</em> the requested amount on-chain
    /// (Boltz <c>onchainAmount</c>), so the Lightning invoice is inflated to <c>requested + fee</c>.
    /// Use only where the payer is shown the invoice directly (e.g. a manual BOLT11 scan); this
    /// breaks LNURL-pay / LUD-06 because the invoice no longer matches the requested amount.
    /// </summary>
    Sender
}

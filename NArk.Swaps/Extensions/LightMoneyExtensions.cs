using BTCPayServer.Lightning;
using NBitcoin;

namespace NArk.Swaps.Extensions;

/// <summary>
/// Conversions between BOLT11 millisatoshi amounts and whole-satoshi on-chain amounts.
/// </summary>
public static class LightMoneyExtensions
{
    /// <summary>
    /// Converts a Lightning amount to whole satoshis, rounding a millisatoshi remainder
    /// <em>up</em>.
    /// </summary>
    /// <remarks>
    /// Every swap-side use of this is either "what we ask to be paid" or "what the counterparty
    /// demands we pay", and both must never shrink: truncating 1 000 500 msat to 1 000 sat pins
    /// the swap one satoshi below the invoice, and makes an equality check against the requested
    /// amount pass for two amounts that differ.
    /// </remarks>
    public static Money ToSatoshisRoundingUp(this LightMoney amount) =>
        Money.Satoshis((long)Math.Ceiling(amount.MilliSatoshi / 1000m));
}

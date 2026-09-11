using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace NArk.ArkadeIntents.Evm;

/// <summary>Thrown when an EVM send quote is unsafe to fund.</summary>
/// <param name="message">Refusal reason.</param>
public sealed class EvmSendQuoteException(string message) : Exception(message);

/// <summary>Validates the binding EVM side of a send quote immediately before Arkade funding.</summary>
public static class EvmSendQuoteValidator
{
    /// <summary>Validates wire echoes, configured contracts, amounts, and cross-chain deadlines.</summary>
    /// <param name="request">Original request.</param>
    /// <param name="quote">Binding solver quote.</param>
    /// <param name="policy">Local acceptance policy.</param>
    /// <param name="now">Current Unix time.</param>
    /// <param name="currentBlock">Current EVM block.</param>
    /// <returns>The validated six ERC20Swap values.</returns>
    public static Erc20SwapValues Validate(
        EvmSendRfqRequest request,
        RfqQuote<EvmSendQuoteProfile> quote,
        EvmSendPolicy policy,
        long now,
        BigInteger currentBlock)
    {
        if (now < 0)
            throw new ArgumentOutOfRangeException(nameof(now));
        if (currentBlock < 0)
            throw new ArgumentOutOfRangeException(nameof(currentBlock));
        var configured = policy.Validate();
        EvmWire.RequireHex32(request.RfqId, nameof(request.RfqId));
        EvmWire.RequireNonZeroHex32(request.Profile.PaymentHash, nameof(request.Profile.PaymentHash));
        EvmWire.RequireHex32(request.Profile.ClientRefundPubkey, nameof(request.Profile.ClientRefundPubkey));
        EvmWire.RequireNonZeroAddress(request.Profile.EvmClaimAddress,
            nameof(request.Profile.EvmClaimAddress), lowerCase: true);
        if (request.V != 1 || request.Type != "rfq_request" || request.AmountSide != "from" || request.Amount <= 0)
            Refuse("request is not a current exact-input EVM send RFQ");
        if (quote.V != 1 || quote.Type != "rfq_quote" || quote.RfqId != request.RfqId || quote.Pair != request.Pair)
            Refuse("quote does not match the requested RFQ envelope");
        if (quote.FromAtomicAmount != request.Amount || quote.ToAtomicAmount <= 0)
            Refuse("quote amounts do not match the exact-input request");
        if (now >= quote.ValidUntil || now >= quote.RefundLocktime)
            Refuse("quote or Arkade refund deadline has expired");
        var profile = quote.Profile ?? throw new EvmSendQuoteException("quote has no EVM profile");
        if (profile.PaymentHash != request.Profile.PaymentHash)
            Refuse("quote payment hash does not match the request");
        if (profile.EvmChainId != policy.ChainId)
            Refuse("quote names an unexpected EVM chain");
        if (profile.EvmContractAddress is null
            || NormalizeQuotedAddress(profile.EvmContractAddress) != configured.SwapContractAddress)
            Refuse("quote names an unexpected ERC20Swap contract");
        if (request.Pair != $"arkade:BTC->ethereum:{configured.TokenAddress}")
            Refuse("request pair does not name the configured token");
        if (profile.MinConfirmations != policy.MinConfirmations || profile.MinAgeSeconds != policy.MinAgeSeconds)
            Refuse("quote lock proof policy differs from the configured policy");
        var timeout = profile.EvmTimeoutBlock
            ?? throw new EvmSendQuoteException("quote has no EVM refund height");
        if (timeout <= currentBlock)
            Refuse("EVM refund height has passed");
        if (profile.EvmRefundAddress is null)
            Refuse("quote has no EVM refund address");
        if (profile.ReceiverPkScript is null || !IsP2tr(profile.ReceiverPkScript))
            Refuse("quote receiver_pk_script is not a 34-byte P2TR script");
        var refundAddress = EvmWire.RequireNonZeroAddress(
            profile.EvmRefundAddress, nameof(profile.EvmRefundAddress))
            .ToLowerInvariant();
        EvmWire.RequireHex32(quote.SolverPubkey, nameof(quote.SolverPubkey));

        var remaining = timeout - currentBlock;
        if (EvmSendPolicy.ProductIsLessThan(
            remaining, policy.FastestSecondsPerBlock, policy.MinimumClaimWindowSeconds))
            Refuse("EVM refund height leaves too little claim time at the fastest block cadence");
        var availableUntilArkadeRefund = new BigInteger(quote.RefundLocktime - now)
            - policy.ArkadeRefundMarginSeconds;
        if (availableUntilArkadeRefund < 0 || EvmSendPolicy.ProductIsGreaterThan(
            remaining, policy.SlowestSecondsPerBlock, availableUntilArkadeRefund))
            Refuse("Arkade refund does not follow the latest plausible EVM expiry by the required margin");

        var values = new Erc20SwapValues(
            request.Profile.PaymentHash,
            quote.ToAtomicAmount,
            configured.TokenAddress,
            request.Profile.EvmClaimAddress,
            refundAddress,
            timeout);
        Erc20SwapCodec.SwapKey(values);
        return values;
    }

    private static string NormalizeQuotedAddress(string value) => EvmWire.RequireNonZeroAddress(
        value.StartsWith("0x", StringComparison.Ordinal) ? value : "0x" + value,
        nameof(value), lowerCase: true);

    private static bool IsP2tr(string value) => value.Length == 68 && value.StartsWith("5120", StringComparison.Ordinal)
        && value[4..].All(Uri.IsHexDigit);

    [DoesNotReturn]
    private static void Refuse(string message) => throw new EvmSendQuoteException(message);
}

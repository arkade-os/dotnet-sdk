using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Rfq.Profiles.Evm;

/// <summary>Builds strict current-wire requests for Arkade-to-ERC20 swaps.</summary>
public static class EvmSendProfile
{
    /// <summary>Builds the solver's legacy pair spelling for an ERC20 token.</summary>
    /// <param name="tokenAddress">Canonical lowercase nonzero token address.</param>
    /// <returns>Current legacy pair spelling.</returns>
    public static string Pair(string tokenAddress) =>
        $"arkade:BTC->ethereum:{EvmWire.RequireNonZeroAddress(tokenAddress, nameof(tokenAddress), lowerCase: true)}";

    /// <summary>Builds an exact-input request whose amount is denominated in satoshis.</summary>
    /// <param name="amountSats">Arkade satoshis to lock.</param>
    /// <param name="paymentHash">SHA-256 swap hash.</param>
    /// <param name="evmClaimAddress">ERC20 recipient.</param>
    /// <param name="refundAddress">Arkade refund address.</param>
    /// <param name="clientRefundPubkey">Client x-only refund key.</param>
    /// <param name="tokenAddress">Canonical ERC20 token.</param>
    /// <param name="rfqId">Optional caller-generated correlation id.</param>
    /// <returns>A strict current-wire request.</returns>
    public static EvmSendRfqRequest Request(
        long amountSats,
        string paymentHash,
        string evmClaimAddress,
        string refundAddress,
        string clientRefundPubkey,
        string tokenAddress,
        string? rfqId = null)
    {
        if (amountSats <= 0 || amountSats > 9_007_199_254_740_991L)
            throw new ArgumentOutOfRangeException(nameof(amountSats), "amount must be a positive safe JSON integer");
        EvmWire.RequireNonZeroHex32(paymentHash, nameof(paymentHash));
        var normalizedClaimAddress = EvmWire.RequireNonZeroAddress(evmClaimAddress, nameof(evmClaimAddress))
            .ToLowerInvariant();
        EvmWire.RequireHex32(clientRefundPubkey, nameof(clientRefundPubkey));
        if (string.IsNullOrWhiteSpace(refundAddress) || refundAddress.Length > 200)
            throw new ArgumentException("refund address must contain 1 to 200 characters", nameof(refundAddress));

        return new EvmSendRfqRequest
        {
            RfqId = EvmWire.RequireHex32(rfqId ?? RfqProtocol.NewRfqId(), nameof(rfqId)),
            Pair = Pair(tokenAddress),
            Amount = amountSats,
            Profile = new EvmSendRequestProfile
            {
                PaymentHash = paymentHash,
                EvmClaimAddress = normalizedClaimAddress,
                RefundAddress = refundAddress,
                ClientRefundPubkey = clientRefundPubkey,
            },
        };
    }
}

/// <summary>The strict EVM-send RFQ envelope used by current solvers.</summary>
public sealed class EvmSendRfqRequest
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; } = RfqProtocol.Version;

    /// <summary>Envelope discriminator.</summary>
    public string Type { get; init; } = "rfq_request";

    /// <summary>Client-selected 32-byte correlation identifier, lowercase hex.</summary>
    public required string RfqId { get; init; }

    /// <summary>Legacy solver pair including the token contract address.</summary>
    public required string Pair { get; init; }

    /// <summary>The exact-input side required by the current EVM send corridor.</summary>
    [JsonPropertyName("amount_side")]
    public string AmountSide { get; init; } = "from";

    /// <summary>Satoshis locked on Arkade, encoded as a JSON integer.</summary>
    public long Amount { get; init; }

    /// <summary>Client-controlled EVM and Arkade settlement fields.</summary>
    public required EvmSendRequestProfile Profile { get; init; }
}

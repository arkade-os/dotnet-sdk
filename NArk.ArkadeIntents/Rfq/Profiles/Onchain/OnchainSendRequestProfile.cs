namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>Request fields of the onchain send profile.</summary>
public sealed class OnchainSendRequestProfile
{
    /// <summary>SHA-256 of the client's own preimage (hex) — the secret both legs turn on.</summary>
    /// <remarks>
    /// The client chooses it because the client is the one exposed here: it funds Arkade first and
    /// is repaid on L1, so the secret that releases the L1 side must be the client's to withhold.
    /// </remarks>
    public required string PaymentHash { get; init; }

    /// <summary>The client's x-only key (hex) that claims the L1 HTLC by revealing the preimage.</summary>
    public required string PayoutPubkey { get; init; }

    /// <summary>The client's own Arkade address, where a failed swap refunds itself by covenant.</summary>
    public required string RefundAddress { get; init; }

    /// <summary>
    /// The client's own x-only key (hex), which the Arkade covenant's client-side refund leaves are
    /// built around.
    /// </summary>
    public required string ClientRefundPubkey { get; init; }
}

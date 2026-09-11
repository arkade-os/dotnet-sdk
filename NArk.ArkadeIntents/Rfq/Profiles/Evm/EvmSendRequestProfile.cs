namespace NArk.ArkadeIntents.Rfq.Profiles.Evm;

/// <summary>Client-controlled fields of an Arkade-to-ERC20 quote request.</summary>
public sealed class EvmSendRequestProfile
{
    /// <summary>SHA-256 of the client's 32-byte swap preimage, lowercase hex.</summary>
    public required string PaymentHash { get; init; }

    /// <summary>EVM address that receives the ERC20 claim.</summary>
    public required string EvmClaimAddress { get; init; }

    /// <summary>Arkade address receiving a failed swap's refund.</summary>
    public required string RefundAddress { get; init; }

    /// <summary>Client x-only key committed to the Arkade refund leaves.</summary>
    public required string ClientRefundPubkey { get; init; }
}

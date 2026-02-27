using System.Text.Json.Serialization;

namespace NArk.Swaps.LendaSwap.Models;

// ─── BTC to Arkade ─────────────────────────────────────────────

public class CreateBtcToArkadeRequest
{
    [JsonPropertyName("claim_pk")]
    public required string ClaimPk { get; set; }

    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("refund_pk")]
    public required string RefundPk { get; set; }

    [JsonPropertyName("sats_receive")]
    public required long SatsReceive { get; set; }

    [JsonPropertyName("target_arkade_address")]
    public required string TargetArkadeAddress { get; set; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }

    [JsonPropertyName("referral_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferralCode { get; set; }
}

// ─── Arkade to EVM ─────────────────────────────────────────────

public class CreateArkadeToEvmRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("refund_pk")]
    public required string RefundPk { get; set; }

    [JsonPropertyName("claiming_address")]
    public required string ClaimingAddress { get; set; }

    [JsonPropertyName("target_address")]
    public required string TargetAddress { get; set; }

    [JsonPropertyName("token_address")]
    public required string TokenAddress { get; set; }

    [JsonPropertyName("evm_chain_id")]
    public required string EvmChainId { get; set; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }

    [JsonPropertyName("amount_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AmountIn { get; set; }

    [JsonPropertyName("amount_out")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AmountOut { get; set; }

    [JsonPropertyName("gasless")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Gasless { get; set; }

    [JsonPropertyName("referral_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferralCode { get; set; }
}

// ─── EVM to Arkade ─────────────────────────────────────────────

public class CreateEvmToArkadeRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("receiver_pk")]
    public required string ReceiverPk { get; set; }

    [JsonPropertyName("target_address")]
    public required string TargetAddress { get; set; }

    [JsonPropertyName("token_address")]
    public required string TokenAddress { get; set; }

    [JsonPropertyName("evm_chain_id")]
    public required string EvmChainId { get; set; }

    [JsonPropertyName("user_address")]
    public required string UserAddress { get; set; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }

    [JsonPropertyName("amount_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AmountIn { get; set; }

    [JsonPropertyName("amount_out")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AmountOut { get; set; }

    [JsonPropertyName("referral_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferralCode { get; set; }
}

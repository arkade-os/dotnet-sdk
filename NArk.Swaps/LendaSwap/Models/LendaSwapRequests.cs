using System.Text.Json.Serialization;

namespace NArk.Swaps.LendaSwap.Models;

// ─── BTC to Arkade ─────────────────────────────────────────────

/// <summary>
/// Request to create a BTC on-chain to Arkade swap.
/// </summary>
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

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── Arkade to BTC ─────────────────────────────────────────────

/// <summary>
/// Request to create an Arkade to BTC on-chain swap.
/// </summary>
public class CreateArkadeToBtcRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("refund_pk")]
    public required string RefundPk { get; set; }

    [JsonPropertyName("target_address")]
    public required string TargetAddress { get; set; }

    [JsonPropertyName("claiming_address")]
    public required string ClaimingAddress { get; set; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }

    [JsonPropertyName("amount_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AmountIn { get; set; }

    [JsonPropertyName("amount_out")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AmountOut { get; set; }

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── Lightning to Arkade ───────────────────────────────────────

/// <summary>
/// Request to create a Lightning to Arkade swap.
/// </summary>
public class CreateLightningToArkadeRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("receiver_pk")]
    public required string ReceiverPk { get; set; }

    [JsonPropertyName("target_arkade_address")]
    public required string TargetArkadeAddress { get; set; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }

    [JsonPropertyName("amount_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AmountIn { get; set; }

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── Arkade to Lightning ───────────────────────────────────────

/// <summary>
/// Request to create an Arkade to Lightning swap (generates a BOLT11 invoice).
/// </summary>
public class CreateArkadeToLightningRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("refund_pk")]
    public required string RefundPk { get; set; }

    [JsonPropertyName("claiming_address")]
    public required string ClaimingAddress { get; set; }

    [JsonPropertyName("bolt11_invoice")]
    public required string Bolt11Invoice { get; set; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── Arkade to EVM ─────────────────────────────────────────────

/// <summary>
/// Request to create an Arkade to EVM token swap.
/// </summary>
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

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── EVM to Arkade ─────────────────────────────────────────────

/// <summary>
/// Request to create an EVM token to Arkade swap.
/// </summary>
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

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── Lightning to EVM ──────────────────────────────────────────

/// <summary>
/// Request to create a Lightning to EVM token swap.
/// </summary>
public class CreateLightningToEvmRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

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

    [JsonPropertyName("gasless")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Gasless { get; set; }

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── EVM to Lightning ──────────────────────────────────────────

/// <summary>
/// Request to create an EVM token to Lightning swap.
/// </summary>
public class CreateEvmToLightningRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("bolt11_invoice")]
    public required string Bolt11Invoice { get; set; }

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

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── BTC to EVM ────────────────────────────────────────────────

/// <summary>
/// Request to create a BTC on-chain to EVM token swap.
/// </summary>
public class CreateBtcToEvmRequest
{
    [JsonPropertyName("claim_pk")]
    public required string ClaimPk { get; set; }

    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

    [JsonPropertyName("refund_pk")]
    public required string RefundPk { get; set; }

    [JsonPropertyName("target_address")]
    public required string TargetAddress { get; set; }

    [JsonPropertyName("token_address")]
    public required string TokenAddress { get; set; }

    [JsonPropertyName("evm_chain_id")]
    public required string EvmChainId { get; set; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; set; }

    [JsonPropertyName("sats_receive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SatsReceive { get; set; }

    [JsonPropertyName("gasless")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Gasless { get; set; }

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

// ─── EVM to BTC ────────────────────────────────────────────────

/// <summary>
/// Request to create an EVM token to BTC on-chain swap.
/// </summary>
public class CreateEvmToBtcRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; set; }

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

    [JsonPropertyName("reflink_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReflinkCode { get; set; }
}

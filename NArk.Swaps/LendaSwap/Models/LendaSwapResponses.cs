using System.Text.Json.Serialization;

namespace NArk.Swaps.LendaSwap.Models;

// ─── Token List ────────────────────────────────────────────────

public class TokenListResponse
{
    [JsonPropertyName("btc_tokens")]
    public List<TokenInfo> BtcTokens { get; set; } = [];

    [JsonPropertyName("evm_tokens")]
    public List<TokenInfo> EvmTokens { get; set; } = [];
}

public class TokenInfo
{
    [JsonPropertyName("token_id")]
    public string TokenId { get; set; } = "";

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("decimals")]
    public int Decimals { get; set; }

    [JsonPropertyName("chain")]
    public string Chain { get; set; } = "";
}

// ─── Quote ─────────────────────────────────────────────────────

public class QuoteResponse
{
    [JsonPropertyName("exchange_rate")]
    public string ExchangeRate { get; set; } = "";

    [JsonPropertyName("protocol_fee")]
    public long ProtocolFee { get; set; }

    [JsonPropertyName("protocol_fee_rate")]
    public decimal ProtocolFeeRate { get; set; }

    [JsonPropertyName("network_fee")]
    public long NetworkFee { get; set; }

    [JsonPropertyName("gasless_network_fee")]
    public long GaslessNetworkFee { get; set; }

    [JsonPropertyName("source_amount")]
    public long SourceAmount { get; set; }

    [JsonPropertyName("target_amount")]
    public long TargetAmount { get; set; }

    [JsonPropertyName("min_amount")]
    public long MinAmount { get; set; }

    [JsonPropertyName("max_amount")]
    public long MaxAmount { get; set; }
}

// ─── Swap Response ─────────────────────────────────────────────

public class LendaSwapResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("btc_htlc_address")]
    public string? BtcHtlcAddress { get; set; }

    [JsonPropertyName("arkade_vhtlc_address")]
    public string? ArkadeVhtlcAddress { get; set; }

    [JsonPropertyName("evm_htlc_address")]
    public string? EvmHtlcAddress { get; set; }

    [JsonPropertyName("source_amount")]
    public long SourceAmount { get; set; }

    [JsonPropertyName("target_amount")]
    public long TargetAmount { get; set; }

    [JsonPropertyName("protocol_fee")]
    public long ProtocolFee { get; set; }

    [JsonPropertyName("network_fee")]
    public long NetworkFee { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("btc_locktime")]
    public long? BtcLocktime { get; set; }

    [JsonPropertyName("arkade_locktime")]
    public long? ArkadeLocktime { get; set; }

    [JsonPropertyName("evm_locktime")]
    public long? EvmLocktime { get; set; }

    [JsonPropertyName("hash_lock")]
    public string? HashLock { get; set; }

    [JsonPropertyName("server_pk")]
    public string? ServerPk { get; set; }

    [JsonPropertyName("evm_chain_id")]
    public string? EvmChainId { get; set; }

    [JsonPropertyName("token_address")]
    public string? TokenAddress { get; set; }
}

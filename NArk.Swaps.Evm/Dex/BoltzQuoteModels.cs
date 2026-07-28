using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.Swaps.Evm.Dex;

/// <summary>
/// A single quote from Boltz's <c>GET /v2/quote/{currency}/in</c>/<c>/out</c> endpoints for
/// swapping between two tokens on the same EVM chain.
/// </summary>
/// <remarks>
/// <see cref="Data"/> is an opaque, DEX-specific blob (the OpenAPI spec declares it
/// <c>additionalProperties: true</c> with no fixed shape) — round-tripped unchanged into
/// <see cref="EncodeQuoteRequest.Data"/>, never inspected on our side.
/// </remarks>
public record TokenQuoteResponse
{
    /// <summary>
    /// For <c>/in</c>: the resulting output amount. For <c>/out</c>: the required input amount.
    /// Decimal string (not hex) — same convention as other Boltz amount fields.
    /// </summary>
    [JsonPropertyName("quote")]
    public required string Quote { get; init; }

    /// <summary>Opaque DEX-specific data to pass back to <c>/encode</c> unchanged.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }
}

/// <summary>Request body for <c>POST /v2/quote/{currency}/encode</c>.</summary>
public record EncodeQuoteRequest
{
    /// <summary>Address that should receive the swap's output — the Router contract itself,
    /// since Router executes these calls in its own context before locking/sweeping the
    /// resulting balance (see <see cref="RouterClient"/>'s lock/claim methods).</summary>
    [JsonPropertyName("recipient")]
    public required string Recipient { get; init; }

    /// <summary>Decimal string.</summary>
    [JsonPropertyName("amountIn")]
    public required string AmountIn { get; init; }

    /// <summary>Decimal string — slippage-protected floor, mirrors Boltz's own reference SDK's
    /// <c>calculateAmountOutMin</c> helper.</summary>
    [JsonPropertyName("amountOutMin")]
    public required string AmountOutMin { get; init; }

    /// <summary>The exact <see cref="TokenQuoteResponse.Data"/> value from the quote being encoded.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }
}

/// <summary>
/// A single contract call, as Boltz's encoder returns it — maps directly onto Router's
/// <c>Call{target,value,callData}</c> struct (<see cref="NArk.Swaps.Evm.Contracts.Router.Call"/>).
/// </summary>
public record EncodedCall
{
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>Decimal string (native-token value to send with the call — normally "0" for an
    /// ERC20-to-ERC20 swap).</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>Hex-encoded calldata, "0x"-prefixed or not.</summary>
    [JsonPropertyName("data")]
    public required string Data { get; init; }
}

/// <summary>Response body for <c>POST /v2/quote/{currency}/encode</c>.</summary>
public record EncodeQuoteResponse
{
    [JsonPropertyName("calls")]
    public required IReadOnlyList<EncodedCall> Calls { get; init; }
}

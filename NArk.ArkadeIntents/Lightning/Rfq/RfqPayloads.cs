using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NArk.ArkadeIntents.Lightning.Rfq;

/// <summary>
/// The RFQ v1 message family — the negotiation layer every Arkade swap corridor speaks.
/// </summary>
/// <remarks>
/// These payloads are the contract; a transport maps them to its own framing but never reshapes
/// them, so the same bytes go over HTTP or a relay. Two asymmetric rules from the spec are encoded
/// here deliberately: <b>requests are strict</b> (a solver rejects unknown fields with
/// <see cref="RfqRefusalReason.UnsupportedPayload"/>, so we serialize exactly the declared fields),
/// while <b>responses are tolerant</b> (we must ignore unknown fields, which is what lets solvers
/// extend responses without a version bump).
/// </remarks>
public static class RfqProtocol
{
    /// <summary>The protocol version this client speaks.</summary>
    public const int Version = 1;

    /// <summary>The pair for the Arkade → Lightning send leg: pay a BOLT11 out of an Arkade balance.</summary>
    public const string SendPair = "arkade:BTC->lightning:BTC";

    /// <summary>
    /// Serializer settings for the whole family: snake_case names, and unknown members ignored on
    /// the way in — the spec's "responses are tolerant" rule.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// A fresh client-chosen negotiation id: 32 random bytes, lowercase hex. It is the idempotency
    /// and correlation key for the whole negotiation, so it must be unpredictable per swap.
    /// </summary>
    /// <returns>64 lowercase hex characters.</returns>
    public static string NewRfqId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

/// <summary>Which side of the pair the request's amount is fixed on.</summary>
[JsonConverter(typeof(RfqAmountSideConverter))]
public enum RfqAmountSide
{
    /// <summary>Exact-in: the client fixes what it pays.</summary>
    From,

    /// <summary>Exact-out: the client fixes what it receives.</summary>
    To,
}

/// <summary>
/// The closed refusal set of RFQ v1. Anything outside it deserializes to
/// <see cref="Unknown"/> — the spec requires clients to treat an unrecognised reason as a generic
/// decline and to infer no retry semantics from it.
/// </summary>
[JsonConverter(typeof(RfqRefusalReasonConverter))]
public enum RfqRefusalReason
{
    /// <summary>A reason outside the closed set: a generic decline.</summary>
    Unknown,

    /// <summary>Pair not served (includes the wrong network for the asset).</summary>
    UnsupportedPair,

    /// <summary>Malformed request, unknown fields, or a profile input the solver cannot serve.</summary>
    UnsupportedPayload,

    /// <summary>Outside the solver's min/max for the pair.</summary>
    AmountOutOfRange,

    /// <summary>The solver is at aggregate capacity right now.</summary>
    ExposureCap,

    /// <summary>The supplied invoice is expired, or expires too soon to swap safely.</summary>
    InvoiceExpired,

    /// <summary>The id or natural key is already bound to a different or already-progressed negotiation.</summary>
    QuoteConflict,

    /// <summary>The solver cannot price the pair right now (no market data).</summary>
    PricingUnavailable,
}

/// <summary>The lifecycle vocabulary RFQ v1 shares across all settlement profiles.</summary>
[JsonConverter(typeof(RfqStateConverter))]
public enum RfqState
{
    /// <summary>A state this client does not know — treat as non-terminal and keep watching the chain.</summary>
    Unknown,

    /// <summary>Terms declined pre-contract; no exposure ever existed.</summary>
    Refused,

    /// <summary>Binding terms issued; awaiting funding until <c>valid_until</c>.</summary>
    Quoted,

    /// <summary><c>valid_until</c> passed with no funding observed.</summary>
    Expired,

    /// <summary>The settlement contract is funded.</summary>
    Funded,

    /// <summary>The solver's outbound fill is in flight.</summary>
    Filling,

    /// <summary>The fill succeeded; the receipt exists and the solver is collecting.</summary>
    Filled,

    /// <summary>Both sides done; the preimage receipt is published.</summary>
    Settled,

    /// <summary>The contract's refund path executed.</summary>
    Refunded,

    /// <summary>Exposure exists and progress is impossible without a human.</summary>
    Stuck,
}

/// <summary>State-vocabulary helpers.</summary>
public static class RfqStateExtensions
{
    /// <summary>True for states after which nothing further will happen.</summary>
    /// <param name="state">The state to classify.</param>
    /// <returns><c>true</c> when the negotiation has reached a terminal state.</returns>
    public static bool IsTerminal(this RfqState state) => state
        is RfqState.Settled or RfqState.Refused or RfqState.Expired or RfqState.Refunded or RfqState.Stuck;
}

/// <summary>Per-profile fields of an <see cref="RfqRequest"/> for the Lightning send leg.</summary>
public sealed class RfqSendRequestProfile
{
    /// <summary>The BOLT11 to pay. Its amount is authoritative, which is what forces exact-out.</summary>
    public required string Invoice { get; init; }

    /// <summary>The client's own Arkade address, where a failed swap refunds itself by covenant.</summary>
    public required string RefundAddress { get; init; }
}

/// <summary>A request for a quote. Strict on the wire: only these fields, or the solver refuses it.</summary>
public sealed class RfqRequest
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; } = RfqProtocol.Version;

    /// <summary>Envelope discriminator.</summary>
    public string Type { get; init; } = "rfq_request";

    /// <summary>The client-chosen correlation id (64 lowercase hex).</summary>
    public required string RfqId { get; init; }

    /// <summary>The directional pair being requested.</summary>
    public required string Pair { get; init; }

    /// <summary>Which leg <see cref="Amount"/> refers to.</summary>
    public required RfqAmountSide AmountSide { get; init; }

    /// <summary>
    /// The amount in base units of the named leg. Omitted for BOLT11 profiles, where the invoice
    /// is authoritative — sending a value that disagrees with it is <c>unsupported_payload</c>.
    /// </summary>
    public long? Amount { get; init; }

    /// <summary>The per-profile fields.</summary>
    public required RfqSendRequestProfile Profile { get; init; }

    /// <summary>
    /// Build a send-leg request. A BOLT11 profile is always exact-out and omits the amount, so the
    /// invoice alone fixes what the solver must pay.
    /// </summary>
    /// <param name="invoice">The BOLT11 to be paid.</param>
    /// <param name="refundAddress">The client's own Arkade refund address.</param>
    /// <param name="rfqId">The correlation id; a fresh one is generated when omitted.</param>
    /// <returns>The request payload, ready for a transport.</returns>
    public static RfqRequest ForSend(string invoice, string refundAddress, string? rfqId = null) => new()
    {
        RfqId = rfqId ?? RfqProtocol.NewRfqId(),
        Pair = RfqProtocol.SendPair,
        AmountSide = RfqAmountSide.To,
        Profile = new RfqSendRequestProfile { Invoice = invoice, RefundAddress = refundAddress },
    };
}

/// <summary>Compare-only and informational fields of a quote. Never trusted — see <see cref="RfqQuote"/>.</summary>
public sealed class RfqQuoteProfile
{
    /// <summary>The invoice's payment hash, echoed back.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>
    /// The solver's derivation of the swap contract's address. Compare-only: check it against your
    /// own derivation and refuse to fund on any mismatch.
    /// </summary>
    public string? LockupAddress { get; init; }
}

/// <summary>
/// The solver's quote. Its <b>binding fields</b> — <see cref="SolverPubkey"/>,
/// <see cref="RefundLocktime"/>, <see cref="ValidUntil"/>, <see cref="FromAmount"/> and
/// <see cref="ToAmount"/> — are the only values a client may trust; everything in
/// <see cref="Profile"/> is compare-only.
/// </summary>
/// <remarks>
/// The client derives the settlement contract from its own data (its invoice, its refund
/// destination, the server key from its own connection, the emulator key from its own fetch) and
/// compares. That is what makes a wrong or malicious solver able to produce only terms the client
/// declines, never a contract that traps funds.
/// </remarks>
public sealed class RfqQuote
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; }

    /// <summary>Envelope discriminator.</summary>
    public string? Type { get; init; }

    /// <summary>The correlation id this quote answers.</summary>
    public string? RfqId { get; init; }

    /// <summary>The pair quoted.</summary>
    public string? Pair { get; init; }

    /// <summary>What the client pays, in base units of the from-leg.</summary>
    public long FromAmount { get; init; }

    /// <summary>What the client receives, in base units of the to-leg. The solver's fee is the spread.</summary>
    public long ToAmount { get; init; }

    /// <summary>The solver's x-only settlement key (hex) — the claim leaf's signer.</summary>
    public required string SolverPubkey { get; init; }

    /// <summary>Unix seconds until which the terms bind, provided funding is observed in time.</summary>
    public long ValidUntil { get; init; }

    /// <summary>Unix seconds at which the client's refund path opens.</summary>
    public long RefundLocktime { get; init; }

    /// <summary>Compare-only per-profile fields.</summary>
    public RfqQuoteProfile? Profile { get; init; }
}

/// <summary>A refusal carrying a reason from the closed set.</summary>
public sealed class RfqRefusal
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; }

    /// <summary>Envelope discriminator.</summary>
    public string? Type { get; init; }

    /// <summary>The correlation id refused, when the solver could parse one.</summary>
    public string? RfqId { get; init; }

    /// <summary>Why the solver declined.</summary>
    public RfqRefusalReason Reason { get; init; }

    /// <summary>An optional human-readable elaboration. Never branch on it.</summary>
    public string? Detail { get; init; }
}

/// <summary>Per-profile receipt fields of a status response.</summary>
public sealed class RfqStatusProfile
{
    /// <summary>The invoice's payment hash.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>The swap contract's address as the solver derived it.</summary>
    public string? LockupAddress { get; init; }

    /// <summary>The txid of the solver's claim, once it has claimed.</summary>
    public string? ClaimTxid { get; init; }

    /// <summary>The txid of the covenant refund, if one was pushed.</summary>
    public string? RefundTxid { get; init; }

    /// <summary>Why the swap failed, when it did.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// The payment preimage — the receipt for the invoice. Present only in
    /// <see cref="RfqState.Settled"/>: before that it is the solver's leverage, and on a failed
    /// swap it never exists.
    /// </summary>
    public string? Preimage { get; init; }
}

/// <summary>
/// The solver's view of a negotiation. Best-effort by design: the settlement is equally observable
/// on-chain, and a claim spending the lockup is the ground truth — it carries the preimage in its
/// witness. Treat this as a convenience, never as the money path.
/// </summary>
public sealed class RfqStatus
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; }

    /// <summary>Envelope discriminator.</summary>
    public string? Type { get; init; }

    /// <summary>The correlation id.</summary>
    public string? RfqId { get; init; }

    /// <summary>Where the negotiation stands.</summary>
    public RfqState State { get; init; }

    /// <summary>Unix seconds of the last change.</summary>
    public long UpdatedAt { get; init; }

    /// <summary>Per-profile receipts.</summary>
    public RfqStatusProfile? Profile { get; init; }
}

/// <summary>Maps the amount side to and from its lowercase wire strings.</summary>
internal sealed class RfqAmountSideConverter : JsonConverter<RfqAmountSide>
{
    public override RfqAmountSide Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "from" => RfqAmountSide.From,
            "to" => RfqAmountSide.To,
            var other => throw new JsonException($"unknown amount_side '{other}'"),
        };

    public override void Write(Utf8JsonWriter writer, RfqAmountSide value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == RfqAmountSide.From ? "from" : "to");
}

/// <summary>Maps the closed refusal set to and from its wire strings, degrading unknowns.</summary>
internal sealed class RfqRefusalReasonConverter : JsonConverter<RfqRefusalReason>
{
    public override RfqRefusalReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "unsupported_pair" => RfqRefusalReason.UnsupportedPair,
            "unsupported_payload" => RfqRefusalReason.UnsupportedPayload,
            "amount_out_of_range" => RfqRefusalReason.AmountOutOfRange,
            "exposure_cap" => RfqRefusalReason.ExposureCap,
            "invoice_expired" => RfqRefusalReason.InvoiceExpired,
            "quote_conflict" => RfqRefusalReason.QuoteConflict,
            "pricing_unavailable" => RfqRefusalReason.PricingUnavailable,
            _ => RfqRefusalReason.Unknown,
        };

    public override void Write(Utf8JsonWriter writer, RfqRefusalReason value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            RfqRefusalReason.UnsupportedPair => "unsupported_pair",
            RfqRefusalReason.UnsupportedPayload => "unsupported_payload",
            RfqRefusalReason.AmountOutOfRange => "amount_out_of_range",
            RfqRefusalReason.ExposureCap => "exposure_cap",
            RfqRefusalReason.InvoiceExpired => "invoice_expired",
            RfqRefusalReason.QuoteConflict => "quote_conflict",
            RfqRefusalReason.PricingUnavailable => "pricing_unavailable",
            _ => "unknown",
        });
}

/// <summary>Maps the lifecycle vocabulary to and from its wire strings, degrading unknowns.</summary>
internal sealed class RfqStateConverter : JsonConverter<RfqState>
{
    public override RfqState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "refused" => RfqState.Refused,
            "quoted" => RfqState.Quoted,
            "expired" => RfqState.Expired,
            "funded" => RfqState.Funded,
            "filling" => RfqState.Filling,
            "filled" => RfqState.Filled,
            "settled" => RfqState.Settled,
            "refunded" => RfqState.Refunded,
            "stuck" => RfqState.Stuck,
            _ => RfqState.Unknown,
        };

    public override void Write(Utf8JsonWriter writer, RfqState value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

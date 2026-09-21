using System.Text.Json.Serialization;
using System.Numerics;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// The solver's quote. Its <b>binding fields</b> — <see cref="SolverPubkey"/>,
/// <see cref="RefundLocktime"/>, <see cref="ValidUntil"/>, <see cref="FromAmount"/> and
/// <see cref="ToAmount"/> — are the values a client may trust; everything in
/// <see cref="Profile"/> is compare-only or informational, with one exception named there
/// (an HTLC send quote's <c>refund_without_receiver_delay</c>, which the client builds into its
/// script because it is the one number the client cannot derive).
/// </summary>
/// <typeparam name="TProfile">The corridor's quote-profile shape.</typeparam>
/// <remarks>
/// The client derives the settlement contract from its own data and compares. That is what makes a
/// wrong or malicious solver able to produce only terms the client declines, never a contract that
/// traps funds.
/// </remarks>
public sealed class RfqQuote<TProfile>
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; }

    /// <summary>Envelope discriminator.</summary>
    public string? Type { get; init; }

    /// <summary>The correlation id this quote answers.</summary>
    public string? RfqId { get; init; }

    /// <summary>The pair quoted.</summary>
    public string? Pair { get; init; }

    /// <summary>What the client pays, in atomic units of the from-leg.</summary>
    [JsonIgnore]
    public long FromAmount { get => checked((long)FromAtomicAmount); init => FromAtomicAmount = value; }

    /// <summary>The from-leg amount without narrowing to the sats API's Int64 range.</summary>
    [JsonPropertyName("from_amount"), JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger FromAtomicAmount { get; init; }

    /// <summary>What the client receives, in atomic units of the to-leg. The solver's fee is the spread.</summary>
    [JsonIgnore]
    public long ToAmount { get => checked((long)ToAtomicAmount); init => ToAtomicAmount = value; }

    /// <summary>The to-leg amount without narrowing to the sats API's Int64 range.</summary>
    [JsonPropertyName("to_amount"), JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger ToAtomicAmount { get; init; }

    /// <summary>Optional lower bound on the onchain receive deposit, in atomic units.</summary>
    [JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger? MinFromAmount { get; init; }

    /// <summary>Optional upper bound on the onchain receive deposit, in atomic units.</summary>
    [JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger? MaxFromAmount { get; init; }

    /// <summary>
    /// The dust sats an Arkade asset deposit rides on, when the quote publishes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a fee</b>, and already netted into the two amounts above: returned inside
    /// <see cref="ToAtomicAmount"/> when the payout leg is sats (the client fronted the carrier on
    /// its own deposit) and charged out of <see cref="FromAtomicAmount"/> when the payout leg is an
    /// asset (the solver fronts it at output 0).
    /// </para>
    /// <para>
    /// What it changes is what a client actually sends: an asset deposit carries
    /// <see cref="FromAtomicAmount"/> of the asset <em>plus</em> this many sats. Absent means zero,
    /// and only the Arkade-to-Arkade class publishes it at all.
    /// </para>
    /// </remarks>
    [JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger? CarrierSats { get; init; }

    /// <summary>The solver's x-only settlement key (hex).</summary>
    public required string SolverPubkey { get; init; }

    /// <summary>Unix seconds until which the terms bind, provided funding is observed in time.</summary>
    public long ValidUntil { get; init; }

    /// <summary>
    /// Unix seconds at which the client's refund path opens. HTLC-class profiles only — the atomic
    /// class has nothing to refund.
    /// </summary>
    public long RefundLocktime { get; init; }

    /// <summary>Compare-only corridor-specific fields.</summary>
    public TProfile? Profile { get; init; }
}

using System.Text.Json.Serialization;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// The solver's quote. Its <b>binding fields</b> — <see cref="SolverPubkey"/>,
/// <see cref="RefundLocktime"/>, <see cref="ValidUntil"/>, <see cref="FromAmount"/> and
/// <see cref="ToAmount"/> — are the only values a client may trust; everything in
/// <see cref="Profile"/> is compare-only or informational.
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
    [JsonConverter(typeof(RfqAmountConverter))]
    public long FromAmount { get; init; }

    /// <summary>What the client receives, in atomic units of the to-leg. The solver's fee is the spread.</summary>
    [JsonConverter(typeof(RfqAmountConverter))]
    public long ToAmount { get; init; }

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

using System.Text.Json.Serialization;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// A request for a quote. Strict on the wire: only these fields, or the solver refuses it with
/// <see cref="RfqRefusalReason.UnsupportedPayload"/>.
/// </summary>
/// <typeparam name="TProfile">The corridor's request-profile shape.</typeparam>
public sealed class RfqRequest<TProfile>
{
    /// <summary>Envelope version.</summary>
    public int V { get; init; } = RfqProtocol.Version;

    /// <summary>Envelope discriminator.</summary>
    public string Type { get; init; } = "rfq_request";

    /// <summary>The client-chosen correlation id (64 lowercase hex).</summary>
    public required string RfqId { get; init; }

    /// <summary>The directional pair being requested, as <c>&lt;corridor&gt;:&lt;asset&gt;-&gt;&lt;corridor&gt;:&lt;asset&gt;</c>.</summary>
    public required string Pair { get; init; }

    /// <summary>Which leg <see cref="Amount"/> refers to.</summary>
    public required RfqAmountSide AmountSide { get; init; }

    /// <summary>
    /// The amount in atomic units of the named leg. Omitted by profiles where something else is
    /// authoritative — sending a value that disagrees with it is <c>unsupported_payload</c>.
    /// </summary>
    [JsonConverter(typeof(RfqAmountConverter))]
    public long? Amount { get; init; }

    /// <summary>The corridor-specific fields.</summary>
    public required TProfile Profile { get; init; }
}

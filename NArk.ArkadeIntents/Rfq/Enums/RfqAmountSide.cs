using System.Text.Json.Serialization;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>Which leg of the pair the request's amount is fixed on.</summary>
[JsonConverter(typeof(RfqAmountSideConverter))]
public enum RfqAmountSide
{
    /// <summary>Exact-in: the client fixes what it pays.</summary>
    From,

    /// <summary>Exact-out: the client fixes what it receives.</summary>
    To,
}

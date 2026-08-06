using System.Text.Json.Serialization;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.Rfq;

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

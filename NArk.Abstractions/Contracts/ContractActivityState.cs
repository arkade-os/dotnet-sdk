namespace NArk.Abstractions.Contracts;

/// <summary>
/// Represents the activity state of a contract for tracking/subscription purposes.
/// </summary>
public enum ContractActivityState
{
    /// <summary>
    /// Contract is not actively tracked/subscribed.
    /// </summary>
    Inactive = 0,

    /// <summary>
    /// Contract is actively subscribed and polled for changes.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Contract is temporarily active and will auto-deactivate once funds are received.
    /// Useful for refund addresses that we only need to watch until coins arrive.
    /// </summary>
    AwaitingFundsBeforeDeactivate = 2
}

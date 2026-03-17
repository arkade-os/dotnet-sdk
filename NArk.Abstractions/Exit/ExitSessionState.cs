namespace NArk.Abstractions.Exit;

/// <summary>
/// State machine for unilateral exit sessions.
/// </summary>
public enum ExitSessionState
{
    /// <summary>Broadcasting tree txs root-to-leaf.</summary>
    Broadcasting = 0,

    /// <summary>All tree txs confirmed, waiting for CSV timelock to expire.</summary>
    AwaitingCsvDelay = 1,

    /// <summary>CSV expired, ready to claim funds on-chain.</summary>
    Claimable = 2,

    /// <summary>Claim tx broadcast, awaiting confirmation.</summary>
    Claiming = 3,

    /// <summary>Claim tx confirmed. Exit complete.</summary>
    Completed = 4,

    /// <summary>Unrecoverable error.</summary>
    Failed = 5
}

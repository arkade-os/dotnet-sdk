namespace NArk.Abstractions.Exit;

/// <summary>
/// Tracks the state of a unilateral exit for a single VTXO.
/// </summary>
public record ExitSession(
    string Id,
    string VtxoTxid,
    uint VtxoVout,
    string WalletId,
    string ClaimAddress,
    ExitSessionState State,
    int NextTxIndex,
    string? ClaimTxid,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? FailReason,
    int RetryCount = 0);

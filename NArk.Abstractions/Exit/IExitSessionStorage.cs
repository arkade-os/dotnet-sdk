using NBitcoin;

namespace NArk.Abstractions.Exit;

/// <summary>
/// Storage for unilateral exit sessions.
/// </summary>
public interface IExitSessionStorage
{
#pragma warning disable CS1591
    Task UpsertAsync(ExitSession session, CancellationToken cancellationToken = default);
    Task<ExitSession?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<ExitSession?> GetByVtxoAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExitSession>> GetByStateAsync(ExitSessionState state, CancellationToken cancellationToken = default);
    /// <summary>Returns all sessions not yet completed or failed, optionally filtered by wallet ID.</summary>
    Task<IReadOnlyList<ExitSession>> GetActiveSessionsAsync(string? walletId = null, CancellationToken cancellationToken = default);
#pragma warning restore CS1591
}

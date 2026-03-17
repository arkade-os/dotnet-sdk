using NBitcoin;

namespace NArk.Abstractions.Exit;

/// <summary>
/// Storage for unilateral exit sessions.
/// </summary>
public interface IExitSessionStorage
{
    Task UpsertAsync(ExitSession session, CancellationToken cancellationToken = default);
    Task<ExitSession?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<ExitSession?> GetByVtxoAsync(OutPoint vtxoOutpoint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExitSession>> GetByStateAsync(ExitSessionState state, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExitSession>> GetActiveSessionsAsync(string? walletId = null, CancellationToken cancellationToken = default);
}

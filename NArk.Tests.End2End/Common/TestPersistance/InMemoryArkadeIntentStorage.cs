using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;

namespace NArk.Tests.End2End.TestPersistance;

/// <summary>
/// In-memory <see cref="IArkadeIntentStorage"/> for the corridor E2E suites — the real one is EF Core.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than nested, because it was already nested twice: the Lightning and asset suites
/// each carried a private copy, and the onchain suite would have made a third. A stub duplicated per
/// fixture is a stub that drifts per fixture, and this one implements the filter contract the
/// interface documents — a copy that quietly drops a filter would have its corridor's tests acting
/// on somebody else's intent, which is precisely the failure the interface's own doc warns about.
/// </para>
/// <para>
/// Every filter is applied, deliberately. It is a stub of the storage, not of the query.
/// </para>
/// </remarks>
internal sealed class InMemoryArkadeIntentStorage : IArkadeIntentStorage
{
    private readonly Dictionary<string, ArkadeSwapIntent> _byId = new();
    private readonly Dictionary<string, ArkadeSwapIntentStatus> _committed = new();

    /// <inheritdoc />
    public event EventHandler<ArkadeSwapIntent>? SwapsChanged;

    /// <inheritdoc />
    public event EventHandler? ActiveScriptsChanged;

    /// <inheritdoc />
    public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
        string? id = null,
        ArkadeSwapIntentStatus? status = null,
        ArkadeSwapIntentStatus[]? statuses = null,
        string? swapPkScript = null,
        string[]? walletIds = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ArkadeSwapIntent> q = _byId.Values;
        if (id is not null) q = q.Where(i => i.Id == id);
        if (status is { } s) q = q.Where(i => i.Status == s);
        if (statuses is { Length: > 0 }) q = q.Where(i => statuses.Contains(i.Status));
        if (swapPkScript is { }) q = q.Where(i => i.SwapPkScript == swapPkScript);
        if (walletIds is { Length: > 0 }) q = q.Where(i => walletIds.Contains(i.WalletId));
        if (skip is { } toSkip) q = q.Skip(toSkip);
        if (take is { } toTake) q = q.Take(toTake);
        return Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(q.ToList());
    }

    /// <inheritdoc />
    public Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default)
    {
        _byId[intent.Id] = intent;
        _committed[intent.Id] = intent.Status;
        SwapsChanged?.Invoke(this, intent);
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overridden rather than inherited: rows here are the caller's own objects, so the default
    /// implementation's read finds the status the caller has already written to the field — the very
    /// change it is asking permission to store — and refuses every save. The guard compares against
    /// what was last committed instead, which is what a real backend's row holds.
    /// </remarks>
    public async Task<bool> TrySaveArkadeSwapIntent(
        ArkadeSwapIntent intent, ArkadeSwapIntentStatus expectedStatus,
        CancellationToken cancellationToken = default)
    {
        if (_committed.TryGetValue(intent.Id, out var committed) && committed != expectedStatus)
            return false;

        await SaveArkadeSwapIntent(intent, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> UpdateStatus(
        string swapPkScript,
        ArkadeSwapIntentStatus status,
        string? spentTxid = null,
        CancellationToken cancellationToken = default)
    {
        var intent = _byId.Values.FirstOrDefault(i => i.SwapPkScript == swapPkScript);
        if (intent is null) return Task.FromResult(false);

        intent.Status = status;
        _committed[intent.Id] = status;
        if (spentTxid is { Length: > 0 }) intent.SpentTxid = spentTxid;
        SwapsChanged?.Invoke(this, intent);
        return Task.FromResult(true);
    }
}

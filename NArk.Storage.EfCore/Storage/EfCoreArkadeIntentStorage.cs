using Microsoft.EntityFrameworkCore;
using NArk.Arkade.NonInteractiveSwaps;
using NArk.ArkadeIntents;
using NArk.Storage.EfCore.Entities;
using NBitcoin;

namespace NArk.Storage.EfCore.Storage;

/// <summary>EF Core-backed <see cref="IArkadeIntentStorage"/> for non-interactive swap intents.</summary>
public class EfCoreArkadeIntentStorage : IArkadeIntentStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;

    public event EventHandler<SwapIntent>? SwapsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public EfCoreArkadeIntentStorage(IArkDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyCollection<SwapIntent>> GetSwapIntents(
        SwapIntentStatus? status = null,
        string? swapPkScript = null,
        string[]? walletIds = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Set<SwapIntentEntity>().AsQueryable();

        if (status is { } s)
            query = query.Where(x => x.Status == s);
        if (swapPkScript is not null)
            query = query.Where(x => x.SwapPkScript == swapPkScript);
        if (walletIds is not null)
            query = query.Where(x => walletIds.Contains(x.WalletId));

        query = query.OrderByDescending(x => x.CreatedAt);
        if (skip is { } sk) query = query.Skip(sk);
        if (take is { } tk) query = query.Take(tk);

        var rows = await query.ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToList();
    }

    public async Task SaveSwapIntent(SwapIntent intent, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<SwapIntentEntity>();

        var existing = await set.FirstOrDefaultAsync(x => x.Id == intent.Id, cancellationToken);
        if (existing is null)
            set.Add(ToEntity(intent));
        else
            Apply(intent, existing);

        await db.SaveChangesAsync(cancellationToken);
        Notify(intent);
    }

    public async Task<bool> UpdateStatus(
        string swapPkScript,
        SwapIntentStatus status,
        string? spentTxid = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<SwapIntentEntity>();

        // Race guard lives here: only a pending swap on this script transitions.
        var entity = await set.FirstOrDefaultAsync(
            x => x.SwapPkScript == swapPkScript && x.Status == SwapIntentStatus.Pending,
            cancellationToken);
        if (entity is null)
            return false;

        entity.Status = status;
        if (spentTxid is not null)
            entity.SpentTxid = spentTxid;

        await db.SaveChangesAsync(cancellationToken);
        Notify(ToDomain(entity));
        return true;
    }

    private void Notify(SwapIntent intent)
    {
        SwapsChanged?.Invoke(this, intent);
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static SwapIntent ToDomain(SwapIntentEntity e) => new()
    {
        Id = e.Id,
        WalletId = e.WalletId,
        Type = e.Type,
        OfferAmount = Money.Satoshis(e.OfferAmount),
        WantAmount = Money.Satoshis(e.WantAmount),
        Status = e.Status,
        CreatedAt = e.CreatedAt,
        SwapPkScript = e.SwapPkScript,
        SwapAddress = e.SwapAddress,
        OfferHex = e.OfferHex,
        FromAssetId = e.FromAssetId,
        ToAssetId = e.ToAssetId,
        SpentTxid = e.SpentTxid,
    };

    private static SwapIntentEntity ToEntity(SwapIntent intent)
    {
        var entity = new SwapIntentEntity { Id = intent.Id };
        Apply(intent, entity);
        return entity;
    }

    private static void Apply(SwapIntent i, SwapIntentEntity e)
    {
        e.WalletId = i.WalletId;
        e.Type = i.Type;
        e.OfferAmount = i.OfferAmount.Satoshi;
        e.WantAmount = i.WantAmount.Satoshi;
        e.Status = i.Status;
        e.CreatedAt = i.CreatedAt;
        e.SwapPkScript = i.SwapPkScript;
        e.SwapAddress = i.SwapAddress;
        e.OfferHex = i.OfferHex;
        e.FromAssetId = i.FromAssetId;
        e.ToAssetId = i.ToAssetId;
        e.SpentTxid = i.SpentTxid;
    }
}

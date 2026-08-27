using Microsoft.EntityFrameworkCore;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.Storage.EfCore.Entities;
using NBitcoin;

namespace NArk.Storage.EfCore.Storage;

/// <summary>EF Core-backed <see cref="IArkadeIntentStorage"/> for non-interactive swap intents.</summary>
public class EfCoreArkadeIntentStorage : IArkadeIntentStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;

    public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public EfCoreArkadeIntentStorage(IArkDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
        string? id = null,
        ArkadeSwapIntentStatus? status = null,
        string? swapPkScript = null,
        string[]? walletIds = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Set<ArkadeSwapIntentEntity>().AsQueryable();

        if (id is not null)
            query = query.Where(x => x.Id == id);
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

    public async Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<ArkadeSwapIntentEntity>();

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
        ArkadeSwapIntentStatus status,
        string? spentTxid = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<ArkadeSwapIntentEntity>();

        // Race guard lives here: only a swap still in flight on this script transitions. Refundable
        // counts as in flight — its deposit is unspent and the refund has yet to be pushed — so a
        // Lightning swap that passes its deadline can still record the spend that ends it. Claimable
        // counts too: a receive swap whose claim window closes, or whose lockup the solver reclaims,
        // must be allowed to leave that state or it is watched and retried forever.
        var entity = await set.FirstOrDefaultAsync(
            x => x.SwapPkScript == swapPkScript
                 && (x.Status == ArkadeSwapIntentStatus.Pending
                     || x.Status == ArkadeSwapIntentStatus.Refundable
                     || x.Status == ArkadeSwapIntentStatus.Claimable),
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

    private void Notify(ArkadeSwapIntent intent)
    {
        SwapsChanged?.Invoke(this, intent);
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ArkadeSwapIntent ToDomain(ArkadeSwapIntentEntity e) => new()
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
        MakerDescriptor = e.MakerDescriptor,
        FromAssetId = e.FromAssetId,
        ToAssetId = e.ToAssetId,
        Invoice = e.Invoice,
        PaymentHash = e.PaymentHash,
        HtlcPubkey = e.HtlcPubkey,
        HtlcLocktime = e.HtlcLocktime,
        Preimage = e.Preimage,
        RefundLocktime = e.RefundLocktime,
        SpentTxid = e.SpentTxid,
    };

    private static ArkadeSwapIntentEntity ToEntity(ArkadeSwapIntent intent)
    {
        var entity = new ArkadeSwapIntentEntity { Id = intent.Id };
        Apply(intent, entity);
        return entity;
    }

    private static void Apply(ArkadeSwapIntent i, ArkadeSwapIntentEntity e)
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
        e.MakerDescriptor = i.MakerDescriptor;
        e.FromAssetId = i.FromAssetId;
        e.ToAssetId = i.ToAssetId;
        e.Invoice = i.Invoice;
        e.Preimage = i.Preimage;
        e.PaymentHash = i.PaymentHash;
        e.HtlcPubkey = i.HtlcPubkey;
        e.HtlcLocktime = i.HtlcLocktime;
        e.RefundLocktime = i.RefundLocktime;
        e.SpentTxid = i.SpentTxid;
    }
}

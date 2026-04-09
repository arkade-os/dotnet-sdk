using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Payments;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore.Storage;

public class EfCorePaymentStorage : IPaymentStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;

    public event EventHandler<ArkPayment>? PaymentChanged;

    public EfCorePaymentStorage(IArkDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task SavePayment(ArkPayment payment, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<ArkPaymentEntity>();

        var existing = await set.FirstOrDefaultAsync(
            p => p.PaymentId == payment.PaymentId, cancellationToken);

        if (existing != null)
        {
            existing.Status = payment.Status;
            existing.FailReason = payment.FailReason;
            existing.IntentTxId = payment.IntentTxId;
            existing.SwapId = payment.SwapId;
            existing.OnchainTxId = payment.OnchainTxId;
            existing.Assets = payment.Assets;
            existing.CompletedAt = payment.CompletedAt?.ToUniversalTime();
            existing.Metadata = payment.Metadata;
        }
        else
        {
            set.Add(new ArkPaymentEntity
            {
                PaymentId = payment.PaymentId,
                WalletId = payment.WalletId,
                Recipient = payment.Recipient,
                Amount = (long)payment.Amount,
                Method = payment.Method,
                Status = payment.Status,
                FailReason = payment.FailReason,
                IntentTxId = payment.IntentTxId,
                SwapId = payment.SwapId,
                OnchainTxId = payment.OnchainTxId,
                Assets = payment.Assets,
                Metadata = payment.Metadata,
                CreatedAt = payment.CreatedAt.ToUniversalTime(),
                CompletedAt = payment.CompletedAt?.ToUniversalTime()
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        PaymentChanged?.Invoke(this, payment);
    }

    public async Task<IReadOnlyCollection<ArkPayment>> GetPayments(
        string[]? walletIds = null,
        string[]? paymentIds = null,
        ArkPaymentStatus[]? statuses = null,
        ArkPaymentMethod[]? methods = null,
        string[]? intentTxIds = null,
        string[]? swapIds = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Set<ArkPaymentEntity>().AsQueryable();

        if (walletIds is { Length: > 0 })
            query = query.Where(p => walletIds.Contains(p.WalletId));

        if (paymentIds is { Length: > 0 })
            query = query.Where(p => paymentIds.Contains(p.PaymentId));

        if (statuses is { Length: > 0 })
            query = query.Where(p => statuses.Contains(p.Status));

        if (methods is { Length: > 0 })
            query = query.Where(p => methods.Contains(p.Method));

        if (intentTxIds is { Length: > 0 })
            query = query.Where(p => p.IntentTxId != null && intentTxIds.Contains(p.IntentTxId));

        if (swapIds is { Length: > 0 })
            query = query.Where(p => p.SwapId != null && swapIds.Contains(p.SwapId));

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(p =>
                p.PaymentId.Contains(searchText) ||
                p.Recipient.Contains(searchText));
        }

        query = query.OrderByDescending(p => p.CreatedAt);

        if (skip.HasValue) query = query.Skip(skip.Value);
        if (take.HasValue) query = query.Take(take.Value);

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToPayment).ToList();
    }

    public async Task<bool> UpdatePaymentStatus(
        string walletId,
        string paymentId,
        ArkPaymentStatus status,
        string? failReason = null,
        string? onchainTxId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.Set<ArkPaymentEntity>()
            .FirstOrDefaultAsync(p => p.PaymentId == paymentId && p.WalletId == walletId, cancellationToken);

        if (entity == null) return false;

        entity.Status = status;
        entity.FailReason = failReason;
        if (onchainTxId != null) entity.OnchainTxId = onchainTxId;
        if (status is ArkPaymentStatus.Completed or ArkPaymentStatus.Failed or ArkPaymentStatus.Cancelled)
            entity.CompletedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        PaymentChanged?.Invoke(this, MapToPayment(entity));
        return true;
    }

    private static ArkPayment MapToPayment(ArkPaymentEntity e) => new(
        PaymentId: e.PaymentId,
        WalletId: e.WalletId,
        Recipient: e.Recipient,
        Amount: (ulong)e.Amount,
        Method: e.Method,
        Status: e.Status,
        FailReason: e.FailReason,
        CreatedAt: e.CreatedAt,
        CompletedAt: e.CompletedAt)
    {
        IntentTxId = e.IntentTxId,
        SwapId = e.SwapId,
        OnchainTxId = e.OnchainTxId,
        Assets = e.Assets,
        Metadata = e.Metadata
    };
}

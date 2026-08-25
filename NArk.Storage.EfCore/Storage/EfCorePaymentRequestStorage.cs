using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Payments;
using NArk.Storage.EfCore.Entities;
using NBitcoin;

namespace NArk.Storage.EfCore.Storage;

public class EfCorePaymentRequestStorage : IPaymentRequestStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;

    public event EventHandler<ArkPaymentRequest>? PaymentRequestChanged;

    public EfCorePaymentRequestStorage(IArkDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task SavePaymentRequest(ArkPaymentRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var set = db.Set<ArkPaymentRequestEntity>();

        var existing = await set.FirstOrDefaultAsync(
            r => r.RequestId == request.RequestId, cancellationToken);

        if (existing != null)
        {
            existing.Status = request.Status;
            existing.ReceivedAmount = request.ReceivedAmount.Satoshi;
            existing.Overpayment = request.Overpayment.Satoshi;
            existing.ArkAddress = request.ArkAddress;
            existing.BoardingAddress = request.BoardingAddress;
            existing.LightningInvoice = request.LightningInvoice;
            existing.ContractScripts = request.ContractScripts;
            existing.SwapId = request.SwapId;
            existing.ExpectedAsset = request.ExpectedAsset;
            existing.ReceivedAssets = request.ReceivedAssets;
            existing.Metadata = request.Metadata;
        }
        else
        {
            set.Add(new ArkPaymentRequestEntity
            {
                RequestId = request.RequestId,
                WalletId = request.WalletId,
                Amount = request.Amount?.Satoshi,
                Description = request.Description,
                Status = request.Status,
                ReceivedAmount = request.ReceivedAmount.Satoshi,
                Overpayment = request.Overpayment.Satoshi,
                ArkAddress = request.ArkAddress,
                BoardingAddress = request.BoardingAddress,
                LightningInvoice = request.LightningInvoice,
                ContractScripts = request.ContractScripts,
                SwapId = request.SwapId,
                ExpectedAsset = request.ExpectedAsset,
                ReceivedAssets = request.ReceivedAssets,
                Metadata = request.Metadata,
                CreatedAt = request.CreatedAt.ToUniversalTime(),
                ExpiresAt = request.ExpiresAt?.ToUniversalTime()
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        PaymentRequestChanged?.Invoke(this, request);
    }

    public async Task<IReadOnlyCollection<ArkPaymentRequest>> GetPaymentRequests(
        string[]? walletIds = null,
        string[]? requestIds = null,
        ArkPaymentRequestStatus[]? statuses = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Set<ArkPaymentRequestEntity>().AsQueryable();

        if (walletIds is { Length: > 0 })
            query = query.Where(r => walletIds.Contains(r.WalletId));

        if (requestIds is { Length: > 0 })
            query = query.Where(r => requestIds.Contains(r.RequestId));

        if (statuses is { Length: > 0 })
            query = query.Where(r => statuses.Contains(r.Status));

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(r =>
                r.RequestId.Contains(searchText) ||
                (r.Description != null && r.Description.Contains(searchText)));
        }

        query = query.OrderByDescending(r => r.CreatedAt);

        if (skip.HasValue) query = query.Skip(skip.Value);
        if (take.HasValue) query = query.Take(take.Value);

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToRequest).ToList();
    }

    public async Task<ArkPaymentRequest?> GetPaymentRequestByScript(
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Find pending/partially-paid requests whose ContractScripts JSON contains this script.
        // EF Core translates .Contains on JSON arrays to the appropriate SQL for each provider.
        var entity = await db.Set<ArkPaymentRequestEntity>()
            .Where(r => r.Status == ArkPaymentRequestStatus.Pending ||
                        r.Status == ArkPaymentRequestStatus.PartiallyPaid)
            .Where(r => r.ContractScriptsJson.Contains(script))
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null ? null : MapToRequest(entity);
    }

    public async Task<bool> UpdatePaymentRequestStatus(
        string walletId,
        string requestId,
        ArkPaymentRequestStatus status,
        Money receivedAmount,
        Money? overpayment = null,
        IReadOnlyList<NArk.Abstractions.VTXOs.VtxoAsset>? receivedAssets = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.Set<ArkPaymentRequestEntity>()
            .FirstOrDefaultAsync(r => r.RequestId == requestId && r.WalletId == walletId, cancellationToken);

        if (entity == null) return false;

        entity.Status = status;
        entity.ReceivedAmount = receivedAmount.Satoshi;
        entity.Overpayment = (overpayment ?? Money.Zero).Satoshi;
        if (receivedAssets is not null)
            entity.ReceivedAssets = receivedAssets;

        await db.SaveChangesAsync(cancellationToken);
        PaymentRequestChanged?.Invoke(this, MapToRequest(entity));
        return true;
    }

    private static ArkPaymentRequest MapToRequest(ArkPaymentRequestEntity e) => new(
        RequestId: e.RequestId,
        WalletId: e.WalletId,
        Amount: e.Amount.HasValue ? Money.Satoshis(e.Amount.Value) : null,
        Description: e.Description,
        Status: e.Status,
        ReceivedAmount: Money.Satoshis(e.ReceivedAmount),
        CreatedAt: e.CreatedAt,
        ExpiresAt: e.ExpiresAt)
    {
        ArkAddress = e.ArkAddress,
        BoardingAddress = e.BoardingAddress,
        LightningInvoice = e.LightningInvoice,
        ContractScripts = e.ContractScripts,
        SwapId = e.SwapId,
        Overpayment = Money.Satoshis(e.Overpayment),
        ExpectedAsset = e.ExpectedAsset,
        ReceivedAssets = e.ReceivedAssets,
        Metadata = e.Metadata
    };
}

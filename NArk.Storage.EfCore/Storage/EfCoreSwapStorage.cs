using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore.Entities;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace NArk.Storage.EfCore.Storage;

public class EfCoreSwapStorage : ISwapStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;

    public event EventHandler<ArkSwap>? SwapsChanged;

    public EfCoreSwapStorage(IArkDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task SaveSwap(string walletId, ArkSwap swap, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var swaps = db.Set<ArkSwapEntity>();
        var existing = await swaps.FirstOrDefaultAsync(
            s => s.SwapId == swap.SwapId && s.WalletId == walletId,
            cancellationToken);

        if (existing != null)
        {
            existing.Status = swap.Status;
            existing.Address = swap.Address;
            existing.Metadata = swap.Metadata;
            existing.UpdatedAt = swap.UpdatedAt.ToUniversalTime();
        }
        else
        {
            var entity = new ArkSwapEntity
            {
                SwapId = swap.SwapId,
                WalletId = walletId,
                SwapType = swap.SwapType,
                Invoice = swap.Invoice,
                ExpectedAmount = swap.ExpectedAmount,
                ContractScript = swap.ContractScript,
                Address = swap.Address,
                Status = swap.Status,
                Hash = swap.Hash,
                Metadata = swap.Metadata,
                CreatedAt = swap.CreatedAt.ToUniversalTime(),
                UpdatedAt = swap.UpdatedAt.ToUniversalTime()
            };
            swaps.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        SwapsChanged?.Invoke(this, swap);
    }

    public async Task<IReadOnlyCollection<ArkSwap>> GetSwaps(
        string[]? walletIds = null,
        string[]? swapIds = null,
        bool? active = null,
        ArkSwapType[]? swapTypes = null,
        ArkSwapStatus[]? status = null,
        string[]? contractScripts = null,
        string[]? hashes = null,
        string[]? invoices = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Set<ArkSwapEntity>().AsQueryable();

        if (walletIds is { Length: > 0 })
            query = query.Where(s => walletIds.Contains(s.WalletId));

        if (swapIds is { Length: > 0 })
            query = query.Where(s => swapIds.Contains(s.SwapId));

        if (active == true)
            query = query.Where(s => s.Status == ArkSwapStatus.Pending || s.Status == ArkSwapStatus.Unknown);
        else if (active == false)
            query = query.Where(s => s.Status != ArkSwapStatus.Pending && s.Status != ArkSwapStatus.Unknown);

        if (swapTypes is { Length: > 0 })
            query = query.Where(s => swapTypes.Contains(s.SwapType));

        if (status is { Length: > 0 })
            query = query.Where(s => status.Contains(s.Status));

        if (contractScripts is { Length: > 0 })
            query = query.Where(s => contractScripts.Contains(s.ContractScript));

        if (hashes is { Length: > 0 })
            query = query.Where(s => hashes.Contains(s.Hash));

        if (invoices is { Length: > 0 })
            query = query.Where(s => invoices.Contains(s.Invoice));

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(s =>
                s.SwapId.Contains(searchText) ||
                s.Invoice.Contains(searchText) ||
                s.Hash.Contains(searchText));
        }

        query = query.OrderByDescending(s => s.CreatedAt);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToArkSwap).ToList();
    }

    public async Task<bool> UpdateSwapStatus(
        string walletId,
        string swapId,
        ArkSwapStatus status,
        string? failReason = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var swap = await db.Set<ArkSwapEntity>()
            .FirstOrDefaultAsync(s => s.SwapId == swapId && s.WalletId == walletId, cancellationToken);

        if (swap == null)
            return false;

        swap.Status = status;
        swap.FailReason = failReason;
        swap.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        SwapsChanged?.Invoke(this, MapToArkSwap(swap));
        return true;
    }

    private static ArkSwap MapToArkSwap(ArkSwapEntity entity)
    {
        return new ArkSwap(
            SwapId: entity.SwapId,
            WalletId: entity.WalletId,
            SwapType: entity.SwapType,
            Invoice: entity.Invoice,
            ExpectedAmount: entity.ExpectedAmount,
            ContractScript: entity.ContractScript,
            Address: entity.Address ?? "",
            Status: entity.Status,
            FailReason: entity.FailReason,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            Hash: entity.Hash
        )
        {
            Metadata = entity.Metadata
        };
    }
}

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

        var persistedMetadata = SerializeRouteAndProviderInto(swap);

        if (existing != null)
        {
            existing.Status = swap.Status;
            existing.Address = swap.Address;
            existing.Metadata = persistedMetadata;
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
                Metadata = persistedMetadata,
                CreatedAt = swap.CreatedAt.ToUniversalTime(),
                UpdatedAt = swap.UpdatedAt.ToUniversalTime()
            };
            swaps.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        SwapsChanged?.Invoke(this, swap);
    }

    /// <summary>
    /// Returns a copy of <paramref name="swap"/>'s metadata dictionary with
    /// the strongly-typed <see cref="ArkSwap.ProviderId"/> and
    /// <see cref="ArkSwap.Route"/> serialised under the
    /// <see cref="SwapMetadata"/> well-known keys, so a future restart can
    /// reconstruct them via <see cref="MapToArkSwap"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ArkSwapEntity"/> has no dedicated columns for these fields
    /// yet (see issue #79 review). Until a migration adds them, the existing
    /// jsonb metadata column is the persistence channel — a quick, schema-
    /// free fix that keeps router-level swap restoration working when more
    /// than one provider is registered.
    /// </para>
    /// <para>Always returns a fresh dictionary, never mutates the input.</para>
    /// </remarks>
    private static Dictionary<string, string>? SerializeRouteAndProviderInto(ArkSwap swap)
    {
        if (swap.ProviderId is null && swap.Route is null) return swap.Metadata;

        var copy = swap.Metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(swap.Metadata);

        if (swap.ProviderId is not null)
            copy[SwapMetadata.ProviderId] = swap.ProviderId;

        if (swap.Route is not null)
        {
            copy[SwapMetadata.RouteSourceNetwork] = swap.Route.Source.Network.ToString();
            copy[SwapMetadata.RouteSourceAssetId] = swap.Route.Source.AssetId;
            copy[SwapMetadata.RouteDestinationNetwork] = swap.Route.Destination.Network.ToString();
            copy[SwapMetadata.RouteDestinationAssetId] = swap.Route.Destination.AssetId;
        }

        return copy;
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
        var (providerId, route) = ReadRouteAndProviderFrom(entity.Metadata);

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
            Metadata = entity.Metadata,
            ProviderId = providerId,
            Route = route,
        };
    }

    /// <summary>
    /// Pulls the strongly-typed <see cref="ArkSwap.ProviderId"/> and
    /// <see cref="ArkSwap.Route"/> back out of the jsonb metadata blob written
    /// by <see cref="SerializeRouteAndProviderInto"/>. Returns
    /// <c>(null, null)</c> for legacy rows persisted before the
    /// multi-provider work landed; consumers that need a route or provider
    /// for those rows should fall back to the configured default provider.
    /// </summary>
    private static (string? ProviderId, SwapRoute? Route) ReadRouteAndProviderFrom(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return (null, null);

        metadata.TryGetValue(SwapMetadata.ProviderId, out var providerId);

        SwapRoute? route = null;
        if (metadata.TryGetValue(SwapMetadata.RouteSourceNetwork, out var srcN)
            && metadata.TryGetValue(SwapMetadata.RouteSourceAssetId, out var srcA)
            && metadata.TryGetValue(SwapMetadata.RouteDestinationNetwork, out var dstN)
            && metadata.TryGetValue(SwapMetadata.RouteDestinationAssetId, out var dstA)
            && Enum.TryParse<SwapNetwork>(srcN, out var sourceNetwork)
            && Enum.TryParse<SwapNetwork>(dstN, out var destinationNetwork))
        {
            route = new SwapRoute(
                new SwapAsset(sourceNetwork, srcA),
                new SwapAsset(destinationNetwork, dstA));
        }

        return (providerId, route);
    }
}

using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Contracts;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore.Storage;

public class EfCoreContractStorage : IContractStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;
    private readonly ArkStorageOptions _options;

    public event EventHandler<ArkContractEntity>? ContractsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public EfCoreContractStorage(IArkDbContextFactory dbContextFactory, ArkStorageOptions options)
    {
        _dbContextFactory = dbContextFactory;
        _options = options;
    }

    public async Task<IReadOnlyCollection<ArkContractEntity>> GetContracts(
        string[]? walletIds = null,
        string[]? scripts = null,
        bool? isActive = null,
        string[]? contractTypes = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<ArkWalletContractEntity> query;

        if (!string.IsNullOrEmpty(searchText) && _options.ContractSearchProvider is not null)
        {
            query = _options.ContractSearchProvider(db.Set<ArkWalletContractEntity>(), searchText);
        }
        else
        {
            query = db.Set<ArkWalletContractEntity>().AsQueryable();

            if (!string.IsNullOrEmpty(searchText))
            {
                query = query.Where(c =>
                    c.Script.Contains(searchText) ||
                    c.Type.Contains(searchText));
            }
        }

        if (walletIds is { })
        {
            query = query.Where(c => walletIds.Contains(c.WalletId));
        }

        if (scripts is { })
        {
            var scriptSet = scripts.ToHashSet();
            query = query.Where(c => scriptSet.Contains(c.Script));
        }

        if (isActive.HasValue)
        {
            query = isActive.Value
                ? query.Where(c => c.ActivityState != ContractActivityState.Inactive)
                : query.Where(c => c.ActivityState == ContractActivityState.Inactive);
        }

        if (contractTypes is { })
        {
            query = query.Where(c => contractTypes.Contains(c.Type));
        }

        query = query.OrderByDescending(c => c.CreatedAt);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        var entities = await query.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(MapToArkContractEntity).ToList();
    }

    public async Task SaveContract(
        ArkContractEntity walletEntity,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var contracts = db.Set<ArkWalletContractEntity>();
        var existing = await contracts.FirstOrDefaultAsync(
            c => c.Script == walletEntity.Script && c.WalletId == walletEntity.WalletIdentifier,
            cancellationToken);

        bool changed;
        if (existing != null)
        {
            ApplyUpdate(existing, walletEntity);
            changed = await db.SaveChangesAsync(cancellationToken) > 0;
        }
        else
        {
            contracts.Add(NewEntity(walletEntity));
            try
            {
                changed = await db.SaveChangesAsync(cancellationToken) > 0;
            }
            catch (DbUpdateException)
            {
                // Possibly lost a concurrent first-time insert race on the {Script, WalletId} PK
                // (e.g. setup's synchronous ensure + a WalletSaved handler + background recovery
                // all inserting the same default at once). If the row another writer committed now
                // exists, convert our losing insert into an update against a fresh context; if it
                // doesn't, the failure was something else — let it propagate.
                if (!await RowExistsAsync(walletEntity, cancellationToken))
                    throw;
                changed = await UpdateExistingAfterRaceAsync(walletEntity, cancellationToken);
            }
        }

        if (changed)
        {
            ContractsChanged?.Invoke(this, walletEntity);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void ApplyUpdate(ArkWalletContractEntity target, ArkContractEntity source)
    {
        target.ActivityState = source.ActivityState;
        target.Type = source.Type;
        target.ContractData = source.AdditionalData;
        target.Metadata = source.Metadata ?? target.Metadata;
    }

    private static ArkWalletContractEntity NewEntity(ArkContractEntity source) => new()
    {
        Script = source.Script,
        WalletId = source.WalletIdentifier,
        ActivityState = source.ActivityState,
        Type = source.Type,
        ContractData = source.AdditionalData,
        Metadata = source.Metadata,
        CreatedAt = source.CreatedAt
    };

    private async Task<bool> RowExistsAsync(ArkContractEntity walletEntity, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ArkWalletContractEntity>().AsNoTracking().AnyAsync(
            c => c.Script == walletEntity.Script && c.WalletId == walletEntity.WalletIdentifier,
            cancellationToken);
    }

    private async Task<bool> UpdateExistingAfterRaceAsync(
        ArkContractEntity walletEntity, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<ArkWalletContractEntity>().FirstOrDefaultAsync(
            c => c.Script == walletEntity.Script && c.WalletId == walletEntity.WalletIdentifier,
            cancellationToken);
        if (existing is null)
            return false; // raced away again (deleted in between) — nothing to do.

        ApplyUpdate(existing, walletEntity);
        return await db.SaveChangesAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateContractActivityState(
        string walletId,
        string script,
        ContractActivityState activityState,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await db.Set<ArkWalletContractEntity>().FirstOrDefaultAsync(
            c => c.WalletId == walletId && c.Script == script && c.ActivityState != activityState,
            cancellationToken);

        if (contract == null)
            return false;

        contract.ActivityState = activityState;
        await db.SaveChangesAsync(cancellationToken);
        ContractsChanged?.Invoke(this, MapToArkContractEntity(contract));
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> DeleteContract(
        string walletId,
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await db.Set<ArkWalletContractEntity>()
            .FirstOrDefaultAsync(c => c.WalletId == walletId && c.Script == script, cancellationToken);

        if (contract == null)
            return false;

        var mapped = MapToArkContractEntity(contract);
        db.Set<ArkWalletContractEntity>().Remove(contract);
        await db.SaveChangesAsync(cancellationToken);
        ContractsChanged?.Invoke(this, mapped);
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static ArkContractEntity MapToArkContractEntity(ArkWalletContractEntity entity)
    {
        return new ArkContractEntity(
            Script: entity.Script,
            ActivityState: entity.ActivityState,
            Type: entity.Type,
            AdditionalData: entity.ContractData,
            WalletIdentifier: entity.WalletId,
            CreatedAt: entity.CreatedAt
        )
        {
            Metadata = entity.Metadata
        };
    }

    public async Task<int> DeactivateAwaitingContractsByScript(
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var contracts = await db.Set<ArkWalletContractEntity>()
            .Where(c => c.Script == script && c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate)
            .ToListAsync(cancellationToken);

        if (contracts.Count == 0)
            return 0;

        foreach (var contract in contracts)
        {
            contract.ActivityState = ContractActivityState.Inactive;
        }

        var count = await db.SaveChangesAsync(cancellationToken);

        foreach (var contract in contracts)
        {
            ContractsChanged?.Invoke(this, MapToArkContractEntity(contract));
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }

        return count;
    }
}

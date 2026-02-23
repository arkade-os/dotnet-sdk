using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Wallets;
using NArk.Storage.EfCore.Entities;

namespace NArk.Storage.EfCore.Storage;

public class EfCoreWalletStorage : IWalletStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;

    public event EventHandler<ArkWalletInfo>? WalletSaved;
    public event EventHandler<string>? WalletDeleted;

    public EfCoreWalletStorage(IArkDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<ArkWalletInfo> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var wallets = db.Set<ArkWalletEntity>();

        var entity = await wallets.FirstOrDefaultAsync(
            w => w.Id == walletIdentifierOrFingerprint, ct);

        if (entity == null)
        {
            entity = await wallets.FirstOrDefaultAsync(
                w => w.AccountDescriptor != null && w.AccountDescriptor.Contains(walletIdentifierOrFingerprint),
                ct);
        }

        if (entity == null)
            throw new KeyNotFoundException($"Wallet {walletIdentifierOrFingerprint} not found");

        return MapToWalletInfo(entity);
    }

    public async Task<IReadOnlySet<ArkWalletInfo>> LoadAllWallets(CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var entities = await db.Set<ArkWalletEntity>().ToListAsync(ct);
        return entities.Select(MapToWalletInfo).ToHashSet();
    }

    public async Task SaveWallet(ArkWalletInfo wallet, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var wallets = db.Set<ArkWalletEntity>();
        var existing = await wallets.FirstOrDefaultAsync(w => w.Id == wallet.Id, ct);

        if (existing != null)
        {
            existing.Wallet = wallet.Secret;
            existing.LastUsedIndex = wallet.LastUsedIndex;
            if (!string.IsNullOrEmpty(wallet.AccountDescriptor))
                existing.AccountDescriptor ??= wallet.AccountDescriptor;
        }
        else
        {
            wallets.Add(MapToEntity(wallet));
        }

        await db.SaveChangesAsync(ct);
        WalletSaved?.Invoke(this, wallet);
    }

    public async Task UpdateLastUsedIndex(string walletId, int lastUsedIndex, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var wallet = await db.Set<ArkWalletEntity>().FindAsync([walletId], ct);
        if (wallet != null)
        {
            wallet.LastUsedIndex = lastUsedIndex;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<ArkWalletInfo?> GetWalletById(string walletId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var entity = await db.Set<ArkWalletEntity>().FindAsync([walletId], ct);
        return entity is null ? null : MapToWalletInfo(entity);
    }

    public async Task<IReadOnlyList<ArkWalletInfo>> GetWalletsByIds(IEnumerable<string> walletIds, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var idSet = walletIds.ToHashSet();
        var entities = await db.Set<ArkWalletEntity>()
            .Where(w => idSet.Contains(w.Id))
            .ToListAsync(ct);

        return entities.Select(MapToWalletInfo).ToList();
    }

    public async Task<bool> UpsertWallet(ArkWalletInfo wallet, bool updateIfExists = true, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var wallets = db.Set<ArkWalletEntity>();
        var existing = await wallets.FindAsync([wallet.Id], ct);
        bool inserted;

        if (existing == null)
        {
            wallets.Add(MapToEntity(wallet));
            inserted = true;
        }
        else if (updateIfExists)
        {
            existing.Wallet = wallet.Secret;
            existing.WalletDestination = wallet.Destination;
            existing.WalletType = wallet.WalletType;
            existing.AccountDescriptor = wallet.AccountDescriptor;
            existing.LastUsedIndex = wallet.LastUsedIndex;
            inserted = false;
        }
        else
        {
            return false;
        }

        await db.SaveChangesAsync(ct);
        WalletSaved?.Invoke(this, wallet);

        return inserted;
    }

    public async Task<bool> DeleteWallet(string walletId, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var wallet = await db.Set<ArkWalletEntity>()
            .Include(w => w.Contracts)
            .FirstOrDefaultAsync(w => w.Id == walletId, ct);

        if (wallet == null)
            return false;

        var contractScripts = wallet.Contracts.Select(c => c.Script).ToList();

        var vtxos = await db.Set<VtxoEntity>()
            .Where(v => contractScripts.Contains(v.Script))
            .ToListAsync(ct);
        db.Set<VtxoEntity>().RemoveRange(vtxos);

        var intents = await db.Set<ArkIntentEntity>()
            .Include(i => i.IntentVtxos)
            .Where(i => i.WalletId == walletId)
            .ToListAsync(ct);
        db.Set<ArkIntentEntity>().RemoveRange(intents);

        db.Set<ArkWalletEntity>().Remove(wallet);

        await db.SaveChangesAsync(ct);

        WalletDeleted?.Invoke(this, walletId);

        return true;
    }

    public async Task UpdateDestination(string walletId, string? destination, CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var wallet = await db.Set<ArkWalletEntity>().FindAsync([walletId], ct);
        if (wallet == null)
            throw new InvalidOperationException($"Wallet {walletId} not found.");

        wallet.WalletDestination = destination;
        await db.SaveChangesAsync(ct);
    }

    private static ArkWalletInfo MapToWalletInfo(ArkWalletEntity entity)
    {
        return new ArkWalletInfo(
            Id: entity.Id,
            Secret: entity.Wallet,
            Destination: entity.WalletDestination,
            WalletType: entity.WalletType,
            AccountDescriptor: entity.AccountDescriptor,
            LastUsedIndex: entity.LastUsedIndex
        );
    }

    private static ArkWalletEntity MapToEntity(ArkWalletInfo info)
    {
        return new ArkWalletEntity
        {
            Id = info.Id,
            Wallet = info.Secret,
            WalletDestination = info.Destination,
            WalletType = info.WalletType,
            AccountDescriptor = info.AccountDescriptor,
            LastUsedIndex = info.LastUsedIndex
        };
    }
}

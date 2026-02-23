using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Storage.EfCore.Entities;
using NBitcoin;

namespace NArk.Storage.EfCore.Storage;

public class EfCoreVtxoStorage : IVtxoStorage
{
    private readonly IArkDbContextFactory _dbContextFactory;
    private readonly ISafetyService _safetyService;

    public event EventHandler<ArkVtxo>? VtxosChanged;
    public event EventHandler? ActiveScriptsChanged;

    public EfCoreVtxoStorage(IArkDbContextFactory dbContextFactory, ISafetyService safetyService)
    {
        _dbContextFactory = dbContextFactory;
        _safetyService = safetyService;
    }

    public async Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        var outpointKey = $"vtxo-upsert::{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}";

        await using var lockHandle = await _safetyService.LockKeyAsync(outpointKey, cancellationToken);
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var vtxos = db.Set<VtxoEntity>();
        var existing = await vtxos.FirstOrDefaultAsync(
            v => v.TransactionId == vtxo.TransactionId &&
                 v.TransactionOutputIndex == (int)vtxo.TransactionOutputIndex,
            cancellationToken);

        var isNew = existing == null;
        var entity = existing ?? new VtxoEntity();

        entity.TransactionId = vtxo.TransactionId;
        entity.TransactionOutputIndex = (int)vtxo.TransactionOutputIndex;
        entity.Script = vtxo.Script;
        entity.Amount = (long)vtxo.Amount;
        entity.SpentByTransactionId = vtxo.SpentByTransactionId;
        entity.SettledByTransactionId = vtxo.SettledByTransactionId;
        entity.Recoverable = vtxo.Swept;
        entity.SeenAt = vtxo.CreatedAt;
        entity.ExpiresAt = vtxo.ExpiresAt ?? DateTimeOffset.MaxValue;
        entity.Preconfirmed = vtxo.Preconfirmed;
        entity.Unrolled = vtxo.Unrolled;
        entity.CommitmentTxids = vtxo.CommitmentTxids is { Count: > 0 } ? JsonSerializer.Serialize(vtxo.CommitmentTxids) : null;
        entity.ArkTxid = vtxo.ArkTxid;

        if (isNew)
        {
            await vtxos.AddAsync(entity, cancellationToken);
        }

        if (await db.SaveChangesAsync(cancellationToken) > 0)
        {
            VtxosChanged?.Invoke(this, vtxo);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }

        return isNew;
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(
        IReadOnlyCollection<string>? scripts = null,
        IReadOnlyCollection<OutPoint>? outpoints = null,
        string[]? walletIds = null,
        bool includeSpent = false,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Set<VtxoEntity>().AsQueryable();

        if (scripts is { })
        {
            var scriptSet = scripts.ToHashSet();
            query = query.Where(v => scriptSet.Contains(v.Script));
        }

        if (outpoints is { })
        {
            var outpointPairs = outpoints
                .Select(op => $"{op.Hash}{op.N}")
                .ToHashSet();
            query = query.Where(v => outpointPairs.Contains(v.TransactionId + v.TransactionOutputIndex));
        }

        if (walletIds is { })
        {
            var walletScripts = db.Set<ArkWalletContractEntity>()
                .Where(c => walletIds.Contains(c.WalletId))
                .Select(c => c.Script);
            query = query.Where(v => walletScripts.Contains(v.Script));
        }

        if (!includeSpent)
        {
            query = query.Where(v =>
                (v.SpentByTransactionId ?? "").Length == 0 &&
                (v.SettledByTransactionId ?? "").Length == 0);
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            var matchingContractScripts = db.Set<ArkWalletContractEntity>()
                .Where(c => c.Type.Contains(searchText))
                .Select(c => c.Script);

            query = query.Where(v =>
                v.TransactionId.Contains(searchText) ||
                v.Script.Contains(searchText) ||
                matchingContractScripts.Contains(v.Script));
        }

        query = query.OrderByDescending(v => v.SeenAt);

        if (skip.HasValue)
            query = query.Skip(skip.Value);

        if (take.HasValue)
            query = query.Take(take.Value);

        var entities = await query.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(MapToArkVtxo).ToList();
    }

    private static ArkVtxo MapToArkVtxo(VtxoEntity entity)
    {
        return new ArkVtxo(
            Script: entity.Script,
            TransactionId: entity.TransactionId,
            TransactionOutputIndex: (uint)entity.TransactionOutputIndex,
            Amount: (ulong)entity.Amount,
            SpentByTransactionId: entity.SpentByTransactionId,
            SettledByTransactionId: entity.SettledByTransactionId,
            Swept: entity.Recoverable,
            CreatedAt: entity.SeenAt,
            ExpiresAt: entity.ExpiresAt == DateTimeOffset.MaxValue ? null : entity.ExpiresAt,
            ExpiresAtHeight: null,
            Preconfirmed: entity.Preconfirmed,
            Unrolled: entity.Unrolled,
            CommitmentTxids: string.IsNullOrEmpty(entity.CommitmentTxids) ? null : JsonSerializer.Deserialize<List<string>>(entity.CommitmentTxids),
            ArkTxid: entity.ArkTxid
        );
    }
}

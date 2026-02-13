using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NBitcoin;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemorySwapStorage : ISwapStorage
{
    private readonly ConcurrentDictionary<string, HashSet<ArkSwap>> _swaps = new();

    public event EventHandler<ArkSwap>? SwapsChanged;
    public Task SaveSwap(string walletId, ArkSwap swap, CancellationToken cancellationToken = default)
    {
        lock (_swaps)
        {
            if (_swaps.TryGetValue(walletId, out var swaps))
                swaps.Add(swap);
            else
                _swaps[walletId] = [swap];
        }

        // BEWARE: can this cause infinite loop?
        SwapsChanged?.Invoke(this, swap);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<ArkSwap>> GetSwaps(
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
        lock (_swaps)
        {
            var result = walletIds is { Length: > 0 }
                ? walletIds.SelectMany(id => _swaps.TryGet(id)?.ToList() ?? []).ToList()
                : _swaps.Values.SelectMany(s => s).ToList();

            if (swapIds is not null)
            {
                result = result.Where(x => swapIds.Contains(x.SwapId)).ToList();
            }

            if (active is not null)
            {
                result = result.Where(x => active == x.Status.IsActive()).ToList();
            }

            if (swapTypes is { Length: > 0 })
            {
                result = result.Where(x => swapTypes.Contains(x.SwapType)).ToList();
            }

            if (status is { Length: > 0 })
            {
                result = result.Where(x => status.Contains(x.Status)).ToList();
            }

            if (contractScripts is { Length: > 0 })
            {
                result = result.Where(x => contractScripts.Contains(x.ContractScript)).ToList();
            }

            if (hashes is { Length: > 0 })
            {
                result = result.Where(x => hashes.Contains(x.Hash)).ToList();
            }

            if (invoices is { Length: > 0 })
            {
                result = result.Where(x => invoices.Contains(x.Invoice)).ToList();
            }

            if (!string.IsNullOrEmpty(searchText))
            {
                result = result.Where(x =>
                    x.SwapId.Contains(searchText) ||
                    x.Invoice.Contains(searchText) ||
                    x.Hash.Contains(searchText)).ToList();
            }

            // Order by CreatedAt descending
            result = result.OrderByDescending(x => x.CreatedAt).ToList();

            if (skip.HasValue)
            {
                result = result.Skip(skip.Value).ToList();
            }

            if (take.HasValue)
            {
                result = result.Take(take.Value).ToList();
            }

            return Task.FromResult<IReadOnlyCollection<ArkSwap>>(new ReadOnlyCollection<ArkSwap>(result));
        }
    }

    public Task<bool> UpdateSwapStatus(
        string walletId,
        string swapId,
        ArkSwapStatus status,
        string? failReason = null,
        CancellationToken cancellationToken = default)
    {
        lock (_swaps)
        {
            if (!_swaps.TryGetValue(walletId, out var swaps))
                return Task.FromResult(false);

            var swap = swaps.FirstOrDefault(x => x.SwapId == swapId);
            if (swap == null)
                return Task.FromResult(false);

            // Remove old and add updated (since ArkSwap is a record, we need to replace it)
            swaps.Remove(swap);
            var updatedSwap = swap with
            {
                Status = status,
                FailReason = failReason,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            swaps.Add(updatedSwap);

            SwapsChanged?.Invoke(this, updatedSwap);
            return Task.FromResult(true);
        }
    }

    public async Task<IReadOnlyCollection<ArkSwapWithContract>> GetSwapsWithContracts(
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
        // Get swaps using the existing method, then wrap with null contract
        var swaps = await GetSwaps(walletIds, swapIds, active, swapTypes, status,
            contractScripts, hashes, invoices, searchText, skip, take, cancellationToken);
        return swaps.Select(s => new ArkSwapWithContract(s, null)).ToList();
    }

    /// <summary>
    /// Clears all swaps from storage. Used for testing swap restoration.
    /// </summary>
    public void Clear()
    {
        lock (_swaps)
        {
            _swaps.Clear();
        }
    }
}
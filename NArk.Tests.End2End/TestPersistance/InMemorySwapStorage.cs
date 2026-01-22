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

    public Task<ArkSwap> GetSwap(string swapId, CancellationToken cancellationToken = default)
    {
        lock (_swaps)
        {
            return Task.FromResult(_swaps.Values.SelectMany(x => x).First(x => x.SwapId == swapId));
        }
    }

    public Task<IReadOnlyCollection<ArkSwap>> GetSwaps(string? walletId = null, string[]? swapIds = null, bool? active = null,
        CancellationToken cancellationToken = default)
    {
        
        lock (_swaps)
        {
            var result = walletId is not null
                ? _swaps.TryGet(walletId)?.ToList()?? []
                : _swaps.Values.SelectMany(s => s).ToList();

            if (swapIds is not null)
            {
                result = result.Where(x => swapIds.Contains(x.SwapId)).ToList();
            }

            if (active is not null)
            {
                result = result.Where(x => active == x.Status.IsActive()).ToList();
            }

            return Task.FromResult<IReadOnlyCollection<ArkSwap>>(new ReadOnlyCollection<ArkSwap>(result));
        }
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
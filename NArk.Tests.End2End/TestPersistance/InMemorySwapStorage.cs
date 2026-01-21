using System.Collections.Concurrent;
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

    public Task<IReadOnlyCollection<ArkSwap>> GetActiveSwaps(string? walletId = null, CancellationToken cancellationToken = default)
    {
        lock (_swaps)
        {
            return walletId is null ?
                Task.FromResult<IReadOnlyCollection<ArkSwap>>(_swaps.Values.SelectMany(s => s).ToList()) :
                Task.FromResult<IReadOnlyCollection<ArkSwap>>(_swaps.TryGet(walletId) ?? []);
        }
    }
}
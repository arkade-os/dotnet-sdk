using System.Runtime.CompilerServices;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core.Transport;

namespace NArk.Core.Sweeper;

public class ServerKeyRotationSweepPolicy(IClientTransport clientTransport): ISweepPolicy
{
    public async IAsyncEnumerable<ArkCoin> SweepAsync(
        IEnumerable<ArkCoin> coins, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        
        var recoverableKeyHexes = serverInfo.DeprecatedSigners
            .Where(ds => ds.Value > now || ds.Value == 0)
            .Select(ds => Convert.ToHexString(ds.Key.ToBytes()))
            .ToHashSet();

        // ECXOnlyPubKey uses reference equality, so compare by hex of the 32-byte x-coordinate.
        var coinsToRefresh = coins
            .Where(v => v.SignerDescriptor is not null &&
                        recoverableKeyHexes.Contains(Convert.ToHexString(v.SignerDescriptor.ToXOnlyPubKey().ToBytes())))
            .ToArray();
        
        if (coinsToRefresh.Length == 0)
        {
            yield break;
        }
        
        foreach (var coin in coinsToRefresh)
        {
            yield return coin;
        }
    }
}

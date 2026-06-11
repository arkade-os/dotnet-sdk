using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core.Transport;

namespace NArk.Core.Sweeper;

public class ServerKeyRotationSweepPolicy(IClientTransport clientTransport): ISweepPolicy
{
    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        
        var recoverableKeys = serverInfo.DeprecatedSigners
            // TODO(11.06.2026) remove this nullable cast after protobuf update, or just remove null check if unused
            .Where(ds=>ds.Value > now || (long?)ds.Value is null)  
            .Select(ds => ds.Key)
            .ToArray();

        var coinsToRefresh = coins
            .Where(v => recoverableKeys.Contains(v.SignerDescriptor?.ToXOnlyPubKey())).ToArray();
        
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
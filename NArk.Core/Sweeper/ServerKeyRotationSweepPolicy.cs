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

        // Match on the contract's SERVER signer key (Contract.Server) — that is the key that
        // rotates and is recorded in the Arkade address. SignerDescriptor holds the USER key,
        // not the server key, so it must NOT be used here. ECXOnlyPubKey uses reference equality,
        // so compare by hex of the 32-byte x-coordinate.
        var coinsToRefresh = coins
            .Where(v => v.Contract.Server is not null &&
                        recoverableKeyHexes.Contains(Convert.ToHexString(v.Contract.Server.ToXOnlyPubKey().ToBytes())))
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

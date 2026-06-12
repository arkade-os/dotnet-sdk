using System.Runtime.CompilerServices;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core.Transport;

namespace NArk.Core.Sweeper;

/// <summary>
/// Sweep policy that selects coins locked under a deprecated Arkade signer key
/// whose collaborative-sweep cutoff has not yet passed (or has no cutoff).
/// <para>
/// When arkd rotates its signing key, existing VTXOs bound to the old signer must be
/// re-enrolled under the current signer before the operator stops co-signing them.
/// This policy surfaces those coins to the sweeper so they are collaboratively swept
/// into a new VTXO under the current signer (regime 1 of the rotation funds model).
/// </para>
/// </summary>
public class ServerKeyRotationSweepPolicy(IClientTransport clientTransport) : ISweepPolicy
{
    public async IAsyncEnumerable<ArkCoin> SweepAsync(
        IEnumerable<ArkCoin> coins,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        var recoverableKeys = serverInfo.DeprecatedSigners
            .Where(ds => ds.Value > now || ds.Value == 0)
            .Select(ds => ds.Key)
            .ToHashSet(ECXOnlyPubKeyComparer.Instance);

        foreach (var coin in coins)
        {
            if (coin.SignerDescriptor != null && recoverableKeys.Contains(coin.SignerDescriptor.ToXOnlyPubKey()))
            {
                yield return coin;
            }
        }
    }
}

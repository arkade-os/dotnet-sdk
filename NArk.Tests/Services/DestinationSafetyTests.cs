using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Services;

[TestFixture]
public class DestinationSafetyTests
{
    [Test]
    public void IsStale_true_when_destination_server_key_is_deprecated()
    {
        var deprecated = NewKey();
        var info = MakeServerInfo(currentSigner: NewKey(), deprecated: [deprecated]);
        var dest = MakeAddress(serverKey: deprecated);
        Assert.That(DestinationSafety.IsStale(dest, info), Is.True);
    }

    [Test]
    public void IsStale_false_when_destination_server_key_is_current()
    {
        var current = NewKey();
        var info = MakeServerInfo(currentSigner: current, deprecated: [NewKey()]);
        var dest = MakeAddress(serverKey: current);
        Assert.That(DestinationSafety.IsStale(dest, info), Is.False);
    }

    [Test]
    public void IsStale_false_for_external_key_not_in_deprecated_set()
    {
        var info = MakeServerInfo(currentSigner: NewKey(), deprecated: [NewKey()]);
        var dest = MakeAddress(serverKey: NewKey());
        Assert.That(DestinationSafety.IsStale(dest, info), Is.False);
    }

    [Test]
    public void IsStale_false_when_destination_null()
        => Assert.That(DestinationSafety.IsStale(null, MakeServerInfo(NewKey(), [])), Is.False);

    private static ECXOnlyPubKey NewKey()
        => ECXOnlyPubKey.Create(new NBitcoin.Key().PubKey.TaprootInternalKey.ToBytes());

    private static OutputDescriptor MakeDescriptor(ECXOnlyPubKey key)
        => key.ToOutputDescriptor(Network.RegTest);

    private static ArkServerInfo MakeServerInfo(ECXOnlyPubKey currentSigner, ECXOnlyPubKey[] deprecated)
    {
        var deprecatedDict = new Dictionary<ECXOnlyPubKey, long>();
        foreach (var key in deprecated)
            deprecatedDict[key] = 0;

        var emptyMultisig = new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());
        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: MakeDescriptor(currentSigner),
            DeprecatedSigners: deprecatedDict,
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: currentSigner,
            CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "test-digest");
    }

    private static ArkAddress MakeAddress(ECXOnlyPubKey serverKey)
    {
        // tweakedKey is the user-side key — use a fresh key so it's distinct from serverKey
        var tweakedKey = NewKey();
        return new ArkAddress(tweakedKey, serverKey, version: 0, isMainnet: false);
    }
}

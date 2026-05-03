using NArk.Arkade.Crypto;
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

[TestFixture]
public class ArkadeNofNMultisigTapScriptTests
{
    [Test]
    public void AugmentsOwnerSet_WithTweakedIntrospectorKey()
    {
        var (alice, bob, introspector) = GenerateThreeKeys();
        var arkadeScript = Convert.FromHexString("51c4c6"); // OP_1 OP_SHA256INITIALIZE OP_SHA256FINALIZE — arbitrary

        var sut = new ArkadeNofNMultisigTapScript(
            arkadeScript,
            baseOwners: [alice, bob],
            introspectorKeys: [introspector]);

        // Augmented owner set = [alice, bob, tweaked(introspector)] in that order.
        Assert.That(sut.AugmentedOwners, Has.Count.EqualTo(3));
        Assert.That(sut.AugmentedOwners[0].ToBytes(), Is.EqualTo(alice.ToBytes()));
        Assert.That(sut.AugmentedOwners[1].ToBytes(), Is.EqualTo(bob.ToBytes()));

        var expectedTweak = ArkadeScriptHash.Tweak(introspector, arkadeScript);
        Assert.That(sut.AugmentedOwners[2].ToBytes(), Is.EqualTo(expectedTweak.ToBytes()));
    }

    [Test]
    public void EmittedScript_MatchesPlainNofNWithAugmentedOwners()
    {
        var (alice, bob, introspector) = GenerateThreeKeys();
        var arkadeScript = Convert.FromHexString("51c4c6");

        var arkade = new ArkadeNofNMultisigTapScript(arkadeScript, [alice, bob], [introspector]);
        var plain = new NofNMultisigTapScript(arkade.AugmentedOwners.ToArray());

        Assert.That(arkade.BuildScript().ToList().Select(o => o.ToBytes()),
            Is.EqualTo(plain.BuildScript().ToList().Select(o => o.ToBytes())));
    }

    [Test]
    public void RejectsEmptyArkadeScript()
    {
        var (alice, _, introspector) = GenerateThreeKeys();
        Assert.Throws<ArgumentException>(() =>
            new ArkadeNofNMultisigTapScript([], [alice], [introspector]));
    }

    [Test]
    public void RejectsEmptyIntrospectorList()
    {
        var (alice, _, _) = GenerateThreeKeys();
        Assert.Throws<ArgumentException>(() =>
            new ArkadeNofNMultisigTapScript([0xc4], [alice], []));
    }

    private static (ECXOnlyPubKey alice, ECXOnlyPubKey bob, TaprootPubKey introspector) GenerateThreeKeys()
    {
        var rng = new Random(42);
        ECXOnlyPubKey Make()
        {
            var seed = new byte[32];
            rng.NextBytes(seed);
            // PubKey.TaprootInternalKey is an x-only-style accessor we can pull bytes from.
            var bytes = new Key(seed).PubKey.TaprootInternalKey.ToBytes();
            return ECXOnlyPubKey.Create(bytes);
        }
        var introspectorSeed = new byte[32];
        rng.NextBytes(introspectorSeed);
        var introspector = new Key(introspectorSeed).PubKey.GetTaprootFullPubKey().OutputKey;
        return (Make(), Make(), introspector);
    }
}

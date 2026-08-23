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
    public void AugmentsOwnerSet_WithTweakedEmulatorKey()
    {
        var (alice, bob, emulator) = GenerateThreeKeys();
        var arkadeScript = Convert.FromHexString("51c4c6"); // OP_1 OP_SHA256INITIALIZE OP_SHA256FINALIZE — arbitrary

        var sut = new ArkadeNofNMultisigTapScript(
            arkadeScript,
            baseOwners: [alice, bob],
            emulatorKeys: [emulator]);

        // Augmented owner set = [alice, bob, tweaked(emulator)] in that order.
        Assert.That(sut.AugmentedOwners, Has.Count.EqualTo(3));
        Assert.That(sut.AugmentedOwners[0].ToBytes(), Is.EqualTo(alice.ToBytes()));
        Assert.That(sut.AugmentedOwners[1].ToBytes(), Is.EqualTo(bob.ToBytes()));

        var expectedTweak = ArkadeTweak.Tweak(emulator, arkadeScript);
        Assert.That(sut.AugmentedOwners[2].ToBytes(), Is.EqualTo(expectedTweak.ToBytes()));
    }

    [Test]
    public void EmittedScript_MatchesPlainNofNWithAugmentedOwners()
    {
        var (alice, bob, emulator) = GenerateThreeKeys();
        var arkadeScript = Convert.FromHexString("51c4c6");

        var arkade = new ArkadeNofNMultisigTapScript(arkadeScript, [alice, bob], [emulator]);
        var plain = new NofNMultisigTapScript(arkade.AugmentedOwners.ToArray());

        Assert.That(arkade.BuildScript().ToList().Select(o => o.ToBytes()),
            Is.EqualTo(plain.BuildScript().ToList().Select(o => o.ToBytes())));
    }

    [Test]
    public void ImplementsArkadeBoundScriptBuilder()
    {
        // Detection by interface is what slices 2/3 of the emulator
        // integration rely on — guard the contract here so that link can't
        // silently break on a refactor.
        var (alice, _, emulator) = GenerateThreeKeys();
        var arkadeScript = new byte[] { 0xc4 };
        var sut = new ArkadeNofNMultisigTapScript(arkadeScript, [alice], [emulator]);

        Assert.That(sut, Is.InstanceOf<IArkadeBoundScriptBuilder>());
        var iface = (IArkadeBoundScriptBuilder)sut;
        Assert.That(iface.ArkadeScript, Is.EqualTo(arkadeScript));
        Assert.That(iface.EmulatorKeys, Has.Count.EqualTo(1));
        Assert.That(iface.EmulatorKeys[0].ToBytes(), Is.EqualTo(emulator.ToBytes()));
    }

    [Test]
    public void RejectsEmptyArkadeScript()
    {
        var (alice, _, emulator) = GenerateThreeKeys();
        Assert.Throws<ArgumentException>(() =>
            new ArkadeNofNMultisigTapScript([], [alice], [emulator]));
    }

    [Test]
    public void RejectsEmptyEmulatorList()
    {
        var (alice, _, _) = GenerateThreeKeys();
        Assert.Throws<ArgumentException>(() =>
            new ArkadeNofNMultisigTapScript([0xc4], [alice], []));
    }

    private static (ECXOnlyPubKey alice, ECXOnlyPubKey bob, TaprootPubKey emulator) GenerateThreeKeys()
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
        var emulatorSeed = new byte[32];
        rng.NextBytes(emulatorSeed);
        var emulator = new Key(emulatorSeed).PubKey.GetTaprootFullPubKey().OutputKey;
        return (Make(), Make(), emulator);
    }
}

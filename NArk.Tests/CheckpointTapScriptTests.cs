using NArk.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests;

[TestFixture]
public class CheckpointTapScriptTests
{
    [Test]
    [TestCase("039e0440b27520b43a8363118c084a04d4f6a50ebfa58e81957f8cceceb2aee0ab64c9fd2d9977ac", SequenceLockType.Time, 605184)]
    [TestCase("516903020040b27520002851fd1c7692e5ab649ce1a88bd8ba59e09401d78c4c8fe6ef93c405c4bbb8ad2008c65c69fb2bb155d81f914de7b0319a01f3ce89eaad8e212efaf835c58010a3ac", SequenceLockType.Time, 1024)]
    [TestCase("51b2752006cf59c1626cb6a9205d0f950bb4b4562adf1372e8719b3c0c4796b9a7870038ad20f97c6a02e7a3055cdb27afb51220efa99d93957a7218da17c665f183696da025ac", SequenceLockType.Height, 1)]
    public void CheckpointTapScriptDecode_DoesNotThrow(string tapScript, SequenceLockType type, long period)
    {
        UnilateralPathArkTapScript script = null!;

        Assert.DoesNotThrow(
            () => script = UnilateralPathArkTapScript.Parse(tapScript)
        );
        Assert.That(script, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(script.Timeout.LockType, Is.EqualTo(type));
            if (type == SequenceLockType.Time)
                Assert.That(script.Timeout.LockPeriod.TotalSeconds, Is.EqualTo(period));
            else
                Assert.That(script.Timeout.LockHeight, Is.EqualTo(period));
        });
    }

    [Test]
    public void CheckpointTapScriptDecode_CanDecodeItsOwnOutput()
    {
        var script =
            new UnilateralPathArkTapScript(
                new Sequence(TimeSpan.FromSeconds(1024)),
                new NofNMultisigTapScript([
                    ECXOnlyPubKey.Create(Convert.FromHexString("18845781f631c48f1c9709e23092067d06837f30aa0cd0544ac887fe91ddd166"))
                ])
            );

        var parsedScript = UnilateralPathArkTapScript.Parse(Convert.ToHexStringLower(script.Build().Script.ToBytes()));

        Assert.Multiple(() =>
        {
            Assert.That(parsedScript.Timeout.LockType, Is.EqualTo(SequenceLockType.Time));
            Assert.That(parsedScript.Timeout.LockPeriod.TotalSeconds, Is.EqualTo(1024));
        });
    }
}
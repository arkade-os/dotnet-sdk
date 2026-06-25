using NArk.Core.Contracts;
using NArk.Abstractions.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

public class VHtlcContractTests
{
    private static readonly OutputDescriptor Server =
        KeyExtensions.ParseOutputDescriptor("03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);
    private static readonly OutputDescriptor Sender =
        KeyExtensions.ParseOutputDescriptor("030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4", Network.RegTest);
    private static readonly OutputDescriptor Receiver =
        KeyExtensions.ParseOutputDescriptor("021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b", Network.RegTest);
    private static readonly byte[] ValidHashBytes = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc");

    // valid[0]: CSV locktime > 16
    [Test]
    public void CanCreateValidContract_CSVLockTimeGt16()
    {
        var contract = new VHTLCContract(Server, Sender, Receiver,
            new uint160(ValidHashBytes, false), new LockTime(265),
            new Sequence(17), new Sequence(144), new Sequence(144));
        Assert.That(contract.GetArkAddress().ToString(false),
            Is.EqualTo("tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63"));
    }

    // valid[1]: CSV locktime <= 16
    [Test]
    public void CanCreateValidContract_CSVLockTimeLte16()
    {
        var contract = new VHTLCContract(Server, Sender, Receiver,
            new uint160(ValidHashBytes, false), new LockTime(265),
            new Sequence(16), new Sequence(144), new Sequence(144));
        Assert.That(contract.GetArkAddress().ToString(false),
            Is.EqualTo("tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3vyn9exe9gjwcjp5ez0wfhhawvvg0xfenzztjmgp3ddrvkwhw04eztqjn6"));
    }

    // valid[2]: seconds CSV timelock
    [Test]
    public void CanCreateValidContract_SecondsCSVLockTime()
    {
        var contract = new VHTLCContract(Server, Sender, Receiver,
            new uint160(ValidHashBytes, false), new LockTime(265),
            new Sequence(TimeSpan.FromSeconds(512)), new Sequence(TimeSpan.FromSeconds(1024)), new Sequence(TimeSpan.FromSeconds(1536)));
        Assert.That(contract.GetArkAddress().ToString(false),
            Is.EqualTo("tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3f354ncawvx3enha2ydyrmactc6fyuvqppsqpl5k63hzupmrl7ndmz8pnu"));
    }

    // invalid[0]: preimage hash too short (19 bytes)
    [Test]
    public void ThrowsOnPreimageHashTooShort()
    {
        var shortHash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251"); // 19 bytes
        Assert.Throws<ArgumentException>(() =>
            VHTLCContract.Create(Server, Sender, Receiver, shortHash, new LockTime(265),
                VHtlcDelay.Blocks(17), VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144)));
    }

    // invalid[1]: preimage hash too long (28 bytes)
    [Test]
    public void ThrowsOnPreimageHashTooLong()
    {
        var longHash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc1234567890abcdef"); // 28 bytes
        Assert.Throws<ArgumentException>(() =>
            VHTLCContract.Create(Server, Sender, Receiver, longHash, new LockTime(265),
                VHtlcDelay.Blocks(17), VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144)));
    }

    // invalid[2]: zero timelock value (unilateralClaimDelay = 0 blocks)
    [Test]
    public void ThrowsOnZeroTimelockValue()
    {
        Assert.Throws<ArgumentException>(() =>
            VHTLCContract.Create(Server, Sender, Receiver, ValidHashBytes, new LockTime(265),
                VHtlcDelay.Blocks(0), VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144)));
    }

    // invalid[3]: refund locktime = 0
    [Test]
    public void ThrowsOnZeroRefundLocktime()
    {
        Assert.Throws<ArgumentException>(() =>
            VHTLCContract.Create(Server, Sender, Receiver, ValidHashBytes, new LockTime(0),
                VHtlcDelay.Blocks(17), VHtlcDelay.Blocks(144), VHtlcDelay.Blocks(144)));
    }

    // invalid[4]: seconds timelock not a multiple of 512 (value = 1000)
    [Test]
    public void ThrowsOnSecondsTimelockNotMultipleOf512()
    {
        Assert.Throws<ArgumentException>(() =>
            VHTLCContract.Create(Server, Sender, Receiver, ValidHashBytes, new LockTime(265),
                VHtlcDelay.Seconds(1000), VHtlcDelay.Seconds(1024), VHtlcDelay.Seconds(1536)));
    }

    // invalid[5]: seconds timelock less than 512 (unilateralRefundDelay = 511)
    [Test]
    public void ThrowsOnSecondsTimelockLessThan512()
    {
        Assert.Throws<ArgumentException>(() =>
            VHTLCContract.Create(Server, Sender, Receiver, ValidHashBytes, new LockTime(265),
                VHtlcDelay.Seconds(512), VHtlcDelay.Seconds(511), VHtlcDelay.Seconds(1536)));
    }
}
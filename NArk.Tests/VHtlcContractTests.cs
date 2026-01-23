using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NBitcoin;

namespace NArk.Tests;

//TODO: implement more from: https://github.com/arkade-os/rust-sdk/blob/master/ark-core/src/vhtlc_fixtures/vhtlc.json
public class VHtlcContractTests
{
    [Test]
    public void CanCreateValidContract_CSVLockTimeGt16()
    {
        var server =
            KeyExtensions.ParseOutputDescriptor(
                "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);
        var sender =
            KeyExtensions.ParseOutputDescriptor("030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
                Network.RegTest);
        var receiver =
            KeyExtensions.ParseOutputDescriptor("021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
                Network.RegTest);
        var hash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc");
        var contract =
            new VHTLCContract(server, sender, receiver, new uint160(hash, false), new LockTime(265), new Sequence(17), new Sequence(144), new Sequence(144));
        Assert.That(contract.GetArkAddress().ToString(false),
            Is.EqualTo(
                "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63"));
    }

    [Test]
    public void CanCreateValidContract_SecondsCSVLockTime()
    {
        var server =
            KeyExtensions.ParseOutputDescriptor(
                "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);
        var sender =
            KeyExtensions.ParseOutputDescriptor("030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
                Network.RegTest);
        var receiver =
            KeyExtensions.ParseOutputDescriptor("021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
                Network.RegTest);
        var hash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc");
        var contract =
            new VHTLCContract(server, sender, receiver, new uint160(hash, false), new LockTime(265), new Sequence(TimeSpan.FromSeconds(512)), new Sequence(TimeSpan.FromSeconds(1024)), new Sequence(TimeSpan.FromSeconds(1536)));
        Assert.That(contract.GetArkAddress().ToString(false),
            Is.EqualTo(
                "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3f354ncawvx3enha2ydyrmactc6fyuvqppsqpl5k63hzupmrl7ndmz8pnu"));
    }
}
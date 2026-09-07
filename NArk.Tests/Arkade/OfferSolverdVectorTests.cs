using NArk.Abstractions.Extensions;
using NArk.ArkadeIntents.Assets;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests.Arkade;

/// <summary>
/// Offer payloads emitted by solverd's own encoder, with the address each derives. If these
/// disagree with this code, this code is wrong: a mismatch means we quote an address the
/// counterparty will not recognise.
/// </summary>
[TestFixture]
public class OfferSolverdVectorTests
{
    private static readonly OutputDescriptor Server = KeyExtensions.ParseOutputDescriptor(
        "024f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", Network.RegTest);

    [TestCase("wantBtc, no exit",
        "0100225120004739eb4ad3eab769b0c2278cd70cce628e20477e8b8c508bcf8519a5f451c9020008000000000000c3500b0022aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05002251203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b10700203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b1080020466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27",
        "tark1qp8n2k7uklxq4aegau7vawtptkgxsja4kt99lpv6krctwpq8tpc65qz8884545l2ka5mps383ntsennz3csywl5t33gghnu9rxjlg5wfv467cj")]
    [TestCase("wantBtc, exit blocks 144",
        "0100225120a928b3f0209a939822b5a73a5d507c9bcc7f68b7875b28b50b5b8412cda16f4d020008000000000000c3500b0022aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05002251203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b10700203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b1080020466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f270c0009000000000000000090",
        "tark1qp8n2k7uklxq4aegau7vawtptkgxsja4kt99lpv6krctwpq8tpc642fgk0czpx5nnq3ttfe6t4g8ex7v0a5t0p6m9z6skkuyztx6zm6d9ccar9")]
    [TestCase("wantAsset, no exit",
        "0100225120b2a1cd158c7a7e2e6346b6d2d0c323eae90d9d6b8c6e2245de4170d953e2bca6020008000000000000c350030022aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05002251203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b10700203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b1080020466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27",
        "tark1qp8n2k7uklxq4aegau7vawtptkgxsja4kt99lpv6krctwpq8tpc64v4pe52cc7n79e35ddkj6rpj86hfpkwkhrrwyfzaustsm9f7909xukfu7j")]
    [TestCase("wantAsset, exit seconds 51200",
        "0100225120a454544d8df3377853e1ac907f9e67c8284780232ecd9ca76fb04af7f87df6bd020008000000000000c350030022aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05002251203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b10700203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b1080020466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f270c000901000000000000c800",
        "tark1qp8n2k7uklxq4aegau7vawtptkgxsja4kt99lpv6krctwpq8tpc64fz523xcmueh0pf7rtys070x0jpgg7qzxtkdnjnklvz27lu8ma4a2sqp7d")]
    [TestCase("wantAsset, exit seconds 604672",
        "0100225120350b08d2374dedf0346be509962c39899ea3b09e9b1b4df572ba3ecb5ae5f6e5020008000000000000c350030022aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05002251203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b10700203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b1080020466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f270c0009010000000000093a00",
        "tark1qp8n2k7uklxq4aegau7vawtptkgxsja4kt99lpv6krctwpq8tpc65dgtprfrwn0d7q6xhegfjckrnzv75wcfaxcmfh6h9w37eddwtah9v56pnn")]
    [TestCase("wantAsset, ratio 1/4",
        "0100225120b2a1cd158c7a7e2e6346b6d2d0c323eae90d9d6b8c6e2245de4170d953e2bca6020008000000000000c350030022aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa09000800000000000000010a0008000000000000000405002251203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b10700203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b1080020466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27",
        "tark1qp8n2k7uklxq4aegau7vawtptkgxsja4kt99lpv6krctwpq8tpc64v4pe52cc7n79e35ddkj6rpj86hfpkwkhrrwyfzaustsm9f7909xukfu7j")]
    [TestCase("wantBtc, ratio 3/8, exit blocks 4032",
        "01002251204a0bd091ba08d9724dcbe74ae91812e8cb3f1a82ff068b8be55c0538842a7fa8020008000000000000c35009000800000000000000030a000800000000000000080b0022aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05002251203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b10700203c72addb4fdf09af94f0c94d7fe92a386a7e70cf8a1d85916386bb2535c7b1b1080020466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f270c0009000000000000000fc0",
        "tark1qp8n2k7uklxq4aegau7vawtptkgxsja4kt99lpv6krctwpq8tpc65jst6zgm5zxewfxuhe62ayvp96xt8udg9lcx3w972hq98zzz5lagzyxac8")]
    public void ADecodedOffer_RederivesTheAddressItNames(string label, string offerHex, string address)
    {
        var offer = OfferCodec.Decode(Convert.FromHexString(offerHex));
        var contract = OfferBuilder.BuildContract(offer, Server, Network.RegTest);

        Assert.Multiple(() =>
        {
            Assert.That(contract.GetArkAddress().ToString(false), Is.EqualTo(address), label);
            // The payload names its own script; deriving a different one is the mismatch that
            // makes an offer unfillable.
            Assert.That(
                Convert.ToHexString(contract.GetScriptPubKey().ToBytes()).ToLowerInvariant(),
                Is.EqualTo(Convert.ToHexString(offer.SwapPkScript).ToLowerInvariant()), label);
        });
    }

    [Test]
    public void EveryVectorRoundTrips()
    {
        foreach (var hex in Vectors())
        {
            var offer = OfferCodec.Decode(Convert.FromHexString(hex));
            Assert.That(Convert.ToHexString(OfferCodec.Encode(offer)).ToLowerInvariant(), Is.EqualTo(hex));
        }
    }

    private static IEnumerable<string> Vectors() =>
        typeof(OfferSolverdVectorTests)
            .GetMethod(nameof(ADecodedOffer_RederivesTheAddressItNames))!
            .GetCustomAttributes(typeof(TestCaseAttribute), false)
            .Cast<TestCaseAttribute>()
            .Select(a => (string)a.Arguments[1]!);
}

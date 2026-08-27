using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Onchain;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// The L1 HTLC both sides of an onchain swap derive independently.
/// </summary>
/// <remarks>
/// Pinned against the counterparty's own derivation rather than against our output, because the
/// whole point is agreement: a byte of drift here is an address one side funds and the other cannot
/// spend, with the money stuck until the refund locktime. See
/// <c>Fixtures/generate-onchain-htlc-vectors.mjs</c> for how the vectors are produced.
/// </remarks>
[TestFixture]
public class OnchainHtlcTests
{
    private static readonly JsonNode Vectors = JsonNode.Parse(
        File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "ArkadeIntents", "Fixtures", "onchain_htlc.json")))!;

    private static JsonNode Inputs => Vectors["inputs"]!;

    private static OnchainHtlc Derive(Network network) => OnchainHtlc.Derive(
        new uint256(Inputs["paymentHash"]!.GetValue<string>()),
        ECXOnlyPubKey.Create(Convert.FromHexString(Inputs["claimKey"]!.GetValue<string>())),
        ECXOnlyPubKey.Create(Convert.FromHexString(Inputs["refundKey"]!.GetValue<string>())),
        Inputs["refundLocktime"]!.GetValue<long>(),
        network);

    [TestCase("bitcoin")]
    [TestCase("testnet")]
    [TestCase("regtest")]
    public void OurDerivation_MatchesTheCounterpartys(string networkName)
    {
        var network = networkName switch
        {
            "bitcoin" => Network.Main,
            "testnet" => Network.TestNet,
            _ => Network.RegTest,
        };
        var expected = Vectors["networks"]![networkName]!;
        var htlc = Derive(network);

        Assert.Multiple(() =>
        {
            Assert.That(htlc.Address.ToString(), Is.EqualTo(expected["address"]!.GetValue<string>()));
            Assert.That(Hex(htlc.PkScript.ToBytes()), Is.EqualTo(expected["pkScript"]!.GetValue<string>()));
            Assert.That(Hex(htlc.ClaimLeaf.ToBytes()), Is.EqualTo(expected["claimLeaf"]!.GetValue<string>()));
            Assert.That(Hex(htlc.RefundLeaf.ToBytes()), Is.EqualTo(expected["refundLeaf"]!.GetValue<string>()));
            Assert.That(Hex(htlc.ClaimControlBlock.ToBytes()),
                Is.EqualTo(expected["claimControlBlock"]!.GetValue<string>()));
            Assert.That(Hex(htlc.RefundControlBlock.ToBytes()),
                Is.EqualTo(expected["refundControlBlock"]!.GetValue<string>()));
        });
    }

    [Test]
    public void AHeightShapedLocktime_IsRefused()
    {
        // The failure this exists to stop is silent: a height builds a refund leaf maturing at block
        // ~500 million, the address is well formed, and the funding confirms.
        Assert.Throws<ArgumentOutOfRangeException>(() => OnchainHtlc.Derive(
            new uint256(Inputs["paymentHash"]!.GetValue<string>()),
            ECXOnlyPubKey.Create(Convert.FromHexString(Inputs["claimKey"]!.GetValue<string>())),
            ECXOnlyPubKey.Create(Convert.FromHexString(Inputs["refundKey"]!.GetValue<string>())),
            850_000,
            Network.RegTest));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

using System.Numerics;
using NArk.ArkadeIntents.Evm;

namespace NArk.Tests.ArkadeIntents.Evm;

public class Erc20SwapCodecTests
{
    private static readonly byte[] Preimage = Enumerable.Repeat((byte)0xaa, 32).ToArray();
    private const string PaymentHash = "e0e77a507412b120f6ede61f62295b1a7b2ff19d3dcc8f7253e51663470c888e";
    private const string Key = "cd55c2bb447cfad0868adf4a67ff88bb604079807cce67c5b48160b4bd6c34dd";

    private static readonly Erc20SwapValues Values = new(
        PaymentHash,
        new BigInteger(1_000_000),
        "0x1111111111111111111111111111111111111111",
        "0x2222222222222222222222222222222222222222",
        "0x3333333333333333333333333333333333333333",
        new BigInteger(12_345));

    [TestCase("amount")]
    [TestCase("timeout")]
    [TestCase("token")]
    [TestCase("claim")]
    [TestCase("refund")]
    public void KeyRefusesZeroContractValues(string field)
    {
        const string zero = "0x0000000000000000000000000000000000000000";
        var values = Values with
        {
            Amount = field == "amount" ? BigInteger.Zero : Values.Amount,
            TimeoutBlock = field == "timeout" ? BigInteger.Zero : Values.TimeoutBlock,
            TokenAddress = field == "token" ? zero : Values.TokenAddress,
            ClaimAddress = field == "claim" ? zero : Values.ClaimAddress,
            RefundAddress = field == "refund" ? zero : Values.RefundAddress,
        };

        Assert.That(() => Erc20SwapCodec.SwapKey(values), Throws.Exception);
    }

    [Test]
    public void KeyRefusesUint256Overflow()
    {
        Assert.That(() => Erc20SwapCodec.SwapKey(Values with { Amount = BigInteger.One << 256 }),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void SwapKey_MatchesTheIndependentIntentSolverVector()
    {
        Assert.That(Convert.ToHexString(Erc20SwapCodec.SwapKey(Values)).ToLowerInvariant(), Is.EqualTo(Key));
    }

    [Test]
    public void SwapsCall_UsesThePinnedSelectorAndKey()
    {
        Assert.That(Convert.ToHexString(Erc20SwapCodec.SwapsCall(Values)).ToLowerInvariant(),
            Is.EqualTo("eb84e7f2" + Key));
        Assert.That(Erc20SwapCodec.ReadSwapsResult(new byte[32]), Is.False);
        var one = new byte[32];
        one[^1] = 1;
        Assert.That(Erc20SwapCodec.ReadSwapsResult(one), Is.True);
        one[^1] = 2;
        Assert.That(() => Erc20SwapCodec.ReadSwapsResult(one), Throws.ArgumentException);
    }

    [Test]
    public void ClaimForCall_MatchesTheIndependentIntentSolverVector()
    {
        const string expected = "bc586b28" +
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
            "00000000000000000000000000000000000000000000000000000000000f4240" +
            "0000000000000000000000001111111111111111111111111111111111111111" +
            "0000000000000000000000002222222222222222222222222222222222222222" +
            "0000000000000000000000003333333333333333333333333333333333333333" +
            "0000000000000000000000000000000000000000000000000000000000003039";

        Assert.That(Convert.ToHexString(Erc20SwapCodec.ClaimForCall(Preimage, Values)).ToLowerInvariant(),
            Is.EqualTo(expected));
    }
}

using System.Numerics;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace NArk.Tests.ArkadeIntents.Evm;

public class EvmSendQuoteValidatorTests
{
    private const string Token = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    private const string Contract = "0x00000000000000000000000000000000deadbeef";
    private const string PaymentHash = "e0e77a507412b120f6ede61f62295b1a7b2ff19d3dcc8f7253e51663470c888e";
    private const long Now = 1_800_000_000;

    [Test]
    public void ValidQuote_ReturnsTheSixContractValuesWithoutNarrowingTheTokenAmount()
    {
        var request = Request();
        var quote = Quote();

        var result = EvmSendQuoteValidator.Validate(request, quote, Policy(), Now, new BigInteger(100));

        Assert.Multiple(() =>
        {
            Assert.That(result.Amount, Is.EqualTo(BigInteger.Parse("100000000000000000000")));
            Assert.That(result.TokenAddress, Is.EqualTo(Token));
            Assert.That(result.TimeoutBlock, Is.EqualTo(new BigInteger(7300)));
        });
    }

    [Test]
    public void CurrentSolverBareContractAddressMatchesTheCanonicalPolicyAddress()
    {
        Assert.That(() => EvmSendQuoteValidator.Validate(
            Request(), Quote(contractAddress: Contract[2..]), Policy(), Now, 100), Throws.Nothing);
    }

    [TestCase("0x0000000000000000000000000000000000000001", 31337, TestName = "Wrong_token_is_refused")]
    [TestCase(Token, 1, TestName = "Wrong_chain_is_refused")]
    public void ConfiguredChainAndTokenAreBinding(string token, long chainId)
    {
        var quote = Quote(chainId: chainId);

        Assert.That(() => EvmSendQuoteValidator.Validate(Request(token), quote, Policy(), Now, 100),
            Throws.TypeOf<EvmSendQuoteException>());
    }

    [Test]
    public void CadenceMustLeaveTheClaimWindowBeforeTheArkadeRefund()
    {
        var quote = Quote(timeoutBlock: 200);

        Assert.That(() => EvmSendQuoteValidator.Validate(Request(), quote, Policy(), Now, 100),
            Throws.TypeOf<EvmSendQuoteException>());
    }

    [TestCase(7_300, false, TestName = "Fractional_fast_cadence_accepts_the_exact_claim_window")]
    [TestCase(7_299, true, TestName = "Fractional_fast_cadence_refuses_below_the_exact_claim_window")]
    public void FractionalCadenceUsesExactBoundaryArithmetic(long timeoutBlock, bool refused)
    {
        var policy = Policy() with { FastestSecondsPerBlock = 0.25m, SlowestSecondsPerBlock = 0.5m };
        var action = () => EvmSendQuoteValidator.Validate(Request(), Quote(timeoutBlock: timeoutBlock),
            policy, Now, 100);

        if (refused) Assert.That(action, Throws.TypeOf<EvmSendQuoteException>());
        else Assert.That(action, Throws.Nothing);
    }

    [Test]
    public void ManuallyConstructedNonCanonicalAddressesAreRefusedBeforeAbiValuesExist()
    {
        var request = new EvmSendRfqRequest
        {
            RfqId = new string('c', 64), Pair = $"arkade:BTC->ethereum:{Token}", Amount = 50_000,
            Profile = new EvmSendRequestProfile
            {
                PaymentHash = PaymentHash,
                EvmClaimAddress = "0x3C44cDdDb6A900fa2b585dd299e03d12FA4293BC",
                RefundAddress = "tark1example",
                ClientRefundPubkey = new string('b', 64),
            },
        };

        Assert.That(() => EvmSendQuoteValidator.Validate(request, Quote(), Policy(), Now, 100),
            Throws.ArgumentException);
    }

    [TestCase(-1L, 100L, TestName = "Negative_now_is_refused")]
    [TestCase(Now, -1L, TestName = "Negative_tip_is_refused")]
    public void NegativeObservationCoordinatesAreRefused(long now, long tip)
    {
        Assert.That(() => EvmSendQuoteValidator.Validate(Request(), Quote(), Policy(), now, tip),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    private static EvmSendPolicy Policy() => new()
    {
        ChainId = 31_337,
        TokenAddress = Token,
        SwapContractAddress = Contract,
        FastestSecondsPerBlock = 1,
        SlowestSecondsPerBlock = 1,
        MinConfirmations = 1,
        MinAgeSeconds = 1,
    };

    private static EvmSendRfqRequest Request(string token = Token) => EvmSendProfile.Request(
        50_000,
        PaymentHash,
        "0x3c44cdddb6a900fa2b585dd299e03d12fa4293bc",
        "tark1example",
        new string('b', 64),
        token,
        new string('c', 64));

    private static RfqQuote<EvmSendQuoteProfile> Quote(
        long chainId = 31_337, long timeoutBlock = 7_300, string contractAddress = Contract) => new()
    {
        V = 1,
        Type = "rfq_quote",
        RfqId = new string('c', 64),
        Pair = $"arkade:BTC->ethereum:{Token}",
        FromAmount = 50_000,
        ToAtomicAmount = BigInteger.Parse("100000000000000000000"),
        SolverPubkey = new string('d', 64),
        ValidUntil = Now + 60,
        RefundLocktime = Now + 14_400,
        Profile = new EvmSendQuoteProfile
        {
            PaymentHash = PaymentHash,
            LockupAddress = "tark1lockup",
            ReceiverPkScript = "5120" + new string('e', 64),
            EvmTimeoutBlock = timeoutBlock,
            EvmRefundAddress = "0xf39fd6e51aad88f6f4ce6ab8827279cfffb92266",
            EvmContractAddress = contractAddress,
            EvmChainId = chainId,
            MinConfirmations = 1,
            MinAgeSeconds = 1,
        },
    };
}

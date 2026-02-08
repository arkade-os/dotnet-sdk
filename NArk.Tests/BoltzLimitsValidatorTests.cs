using Microsoft.Extensions.Options;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;

namespace NArk.Tests;

[TestFixture]
public class BoltzLimitsValidatorTests
{
    private const long SubmarineMin = 10_000;
    private const long SubmarineMax = 25_000_000;
    private const decimal SubmarineFeePercent = 0.1m; // 0.1% as returned by Boltz API
    private const long SubmarineMinerFee = 300;

    private const long ReverseMin = 50_000;
    private const long ReverseMax = 10_000_000;
    private const decimal ReverseFeePercent = 0.5m; // 0.5% as returned by Boltz API
    private const long ReverseClaimFee = 276;

    private BoltzLimitsValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        var submarine = CreateSubmarinePairs(SubmarineMin, SubmarineMax, SubmarineFeePercent, SubmarineMinerFee);
        var reverse = CreateReversePairs(ReverseMin, ReverseMax, ReverseFeePercent, ReverseClaimFee);
        var client = new TestCachedBoltzClient(submarine, reverse);
        _validator = new BoltzLimitsValidator(client);
    }

    [Test]
    public async Task ValidateAmount_RejectsBelow_SubmarineMinimum()
    {
        var (isValid, error) = await _validator.ValidateAmountAsync(SubmarineMin - 1, isReverse: false);

        Assert.That(isValid, Is.False);
        Assert.That(error, Does.Contain("below minimum"));
        Assert.That(error, Does.Contain($"{SubmarineMin}"));
    }

    [Test]
    public async Task ValidateAmount_RejectsAbove_SubmarineMaximum()
    {
        var (isValid, error) = await _validator.ValidateAmountAsync(SubmarineMax + 1, isReverse: false);

        Assert.That(isValid, Is.False);
        Assert.That(error, Does.Contain("exceeds maximum"));
        Assert.That(error, Does.Contain($"{SubmarineMax}"));
    }

    [Test]
    public async Task ValidateAmount_AcceptsWithinRange_Submarine()
    {
        var midAmount = (SubmarineMin + SubmarineMax) / 2;

        var (isValid, error) = await _validator.ValidateAmountAsync(midAmount, isReverse: false);

        Assert.That(isValid, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateAmount_AcceptsWithinRange_Reverse()
    {
        var midAmount = (ReverseMin + ReverseMax) / 2;

        var (isValid, error) = await _validator.ValidateAmountAsync(midAmount, isReverse: true);

        Assert.That(isValid, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateAmount_ReturnsInvalid_WhenPairsUnavailable()
    {
        var client = new TestCachedBoltzClient(submarine: null, reverse: null);
        var validator = new BoltzLimitsValidator(client);

        var (isValid, error) = await validator.ValidateAmountAsync(100_000, isReverse: false);

        Assert.That(isValid, Is.False);
        Assert.That(error, Does.Contain("Unable to fetch"));
    }

    [Test]
    public async Task ValidateFees_AcceptsReasonableFee()
    {
        // Submarine: user pays actualSwapAmount onchain, receives amountSats via Lightning
        // actualFee = actualSwapAmount - amountSats
        // expectedFee = amountSats * (feePercent / 100) + minerFee
        var amountSats = 1_000_000L;

        // Boltz API returns 0.1 meaning 0.1%, so validator divides by 100 to get 0.001
        var expectedFee = (long)(amountSats * (SubmarineFeePercent / 100m)) + SubmarineMinerFee;
        var actualSwapAmount = amountSats + expectedFee;

        var (isValid, error) = await _validator.ValidateFeesAsync(
            amountSats, actualSwapAmount, isReverse: false);

        Assert.That(isValid, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateFees_RejectsExcessiveFee()
    {
        var amountSats = 1_000_000L;

        // Boltz API returns 0.1 meaning 0.1%, so validator divides by 100 to get 0.001
        var expectedFee = (long)(amountSats * (SubmarineFeePercent / 100m)) + SubmarineMinerFee;

        // Make the actual fee far exceed the expected fee + tolerance
        var excessiveFee = expectedFee + BoltzLimitsValidator.FeeToleranceSats + 500;
        var actualSwapAmount = amountSats + excessiveFee;

        var (isValid, error) = await _validator.ValidateFeesAsync(
            amountSats, actualSwapAmount, isReverse: false);

        Assert.That(isValid, Is.False);
        Assert.That(error, Does.Contain("fee verification failed"));
    }

    private static SubmarinePairsResponse CreateSubmarinePairs(
        long min, long max, decimal feePercent, long minerFee)
    {
        return new SubmarinePairsResponse
        {
            ARK = new SubmarinePairInfo
            {
                BTC = new SubmarinePairDetails
                {
                    Limits = new LimitsInfo { Minimal = min, Maximal = max },
                    Fees = new FeeInfo { Percentage = feePercent, MinerFeesValue = minerFee }
                }
            }
        };
    }

    private static ReversePairsResponse CreateReversePairs(
        long min, long max, decimal feePercent, long claimFee)
    {
        return new ReversePairsResponse
        {
            BTC = new ReversePairInfo
            {
                ARK = new ReversePairDetails
                {
                    Limits = new ReverseLimitsInfo { Minimal = min, Maximal = max },
                    Fees = new ReverseFeeInfo
                    {
                        Percentage = feePercent,
                        MinerFees = new ReverseMinerFeesInfo { Claim = claimFee }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Test-only subclass that overrides the virtual pair-fetching methods
    /// so we can supply deterministic test data without hitting any HTTP endpoint.
    /// </summary>
    private class TestCachedBoltzClient : CachedBoltzClient
    {
        private readonly SubmarinePairsResponse? _submarine;
        private readonly ReversePairsResponse? _reverse;

        public TestCachedBoltzClient(
            SubmarinePairsResponse? submarine,
            ReversePairsResponse? reverse)
            : base(
                new HttpClient(),
                Options.Create(new BoltzClientOptions
                {
                    BoltzUrl = "http://localhost",
                    WebsocketUrl = "ws://localhost"
                }))
        {
            _submarine = submarine;
            _reverse = reverse;
        }

        public override Task<SubmarinePairsResponse?> GetSubmarinePairsAsync(
            CancellationToken cancellation = default)
            => Task.FromResult(_submarine);

        public override Task<ReversePairsResponse?> GetReversePairsAsync(
            CancellationToken cancellation = default)
            => Task.FromResult(_reverse);
    }
}

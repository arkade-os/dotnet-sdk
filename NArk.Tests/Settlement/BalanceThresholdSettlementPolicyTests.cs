using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Settlement;
using NArk.Core.Settlement;
using NSubstitute;

namespace NArk.Tests.Settlement;

[TestFixture]
public class BalanceThresholdSettlementPolicyTests
{
    private const string WalletId = "test-wallet";
    private static readonly SettlementDestination Destination = SettlementDestination.Ark("ark1qexample");
    private static readonly TimeHeight CurrentTime = new(DateTimeOffset.UtcNow, 800_000);

    private ISettlementConfigProvider _configProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _configProvider = Substitute.For<ISettlementConfigProvider>();
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>([]));
    }

    private void Configure(params SettlementConfig[] configs) =>
        _configProvider.GetConfigs(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<SettlementConfig>>(configs));

    private static SettlementContext Context(long balanceSats) =>
        new(WalletId, Array.Empty<ArkCoin>(), balanceSats, CurrentTime);

    private async Task<List<SettlementPlan>> Evaluate(long balanceSats)
    {
        var plans = new List<SettlementPlan>();
        await foreach (var plan in new BalanceThresholdSettlementPolicy(_configProvider).EvaluateAsync(Context(balanceSats)))
            plans.Add(plan);
        return plans;
    }

    [Test]
    public async Task DoesNotFire_BelowThreshold()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000));

        Assert.That(await Evaluate(99_999), Is.Empty);
    }

    [Test]
    public async Task SettlesTheWholeBalance_AtOrAboveThreshold()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000));

        var plans = await Evaluate(250_000);

        Assert.That(plans, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            // The threshold gates when a settlement fires, never how much it moves.
            Assert.That(plans[0].AmountSats, Is.EqualTo(250_000));
            Assert.That(plans[0].Destination, Is.EqualTo(Destination));
            Assert.That(plans[0].Coins, Is.Null);
        });
    }

    [Test]
    public async Task CapsAtMaxAmount()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000, MaxAmountSats: 150_000));

        var plans = await Evaluate(250_000);

        Assert.That(plans[0].AmountSats, Is.EqualTo(150_000));
    }

    [Test]
    public async Task IgnoresDisabledRules()
    {
        Configure(new SettlementConfig(WalletId, Destination, 0, Enabled: false));

        Assert.That(await Evaluate(250_000), Is.Empty);
    }

    [Test]
    public async Task IgnoresRulesForOtherWallets()
    {
        Configure(new SettlementConfig("other-wallet", Destination, 0));

        Assert.That(await Evaluate(250_000), Is.Empty);
    }

    [Test]
    public async Task DoesNotFire_OnZeroBalance()
    {
        Configure(new SettlementConfig(WalletId, Destination, 0));

        Assert.That(await Evaluate(0), Is.Empty);
    }

    [Test]
    public async Task YieldsEveryMatchingRule_LowestThresholdFirst()
    {
        var low = SettlementDestination.Ark("ark1qlow");
        var high = SettlementDestination.Ark("ark1qhigh");
        Configure(
            new SettlementConfig(WalletId, high, 200_000, MaxAmountSats: 50_000),
            new SettlementConfig(WalletId, low, 100_000, MaxAmountSats: 50_000));

        var plans = await Evaluate(250_000);

        // Union semantics: both rules get a plan, and the engine executes them against a
        // shrinking balance rather than the policy picking a single winner.
        Assert.That(plans.Select(plan => plan.Destination), Is.EqualTo(new[] { low, high }));
    }
}

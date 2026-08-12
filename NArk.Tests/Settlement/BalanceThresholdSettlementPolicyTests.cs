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

    private BalanceThresholdSettlementPolicy CreatePolicy() => new(_configProvider);

    [Test]
    public async Task DoesNotFire_BelowThreshold()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000));

        var plan = await CreatePolicy().EvaluateAsync(Context(99_999));

        Assert.That(plan, Is.Null);
    }

    [Test]
    public async Task SettlesTheWholeBalance_AtOrAboveThreshold()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000));

        var plan = await CreatePolicy().EvaluateAsync(Context(250_000));

        Assert.That(plan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // The threshold gates when a settlement fires, never how much it moves.
            Assert.That(plan!.AmountSats, Is.EqualTo(250_000));
            Assert.That(plan.Destination, Is.EqualTo(Destination));
            Assert.That(plan.Coins, Is.Null);
        });
    }

    [Test]
    public async Task CapsAtMaxAmount()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000, MaxAmountSats: 150_000));

        var plan = await CreatePolicy().EvaluateAsync(Context(250_000));

        Assert.That(plan!.AmountSats, Is.EqualTo(150_000));
    }

    [Test]
    public async Task IgnoresDisabledRules()
    {
        Configure(new SettlementConfig(WalletId, Destination, 0, Enabled: false));

        var plan = await CreatePolicy().EvaluateAsync(Context(250_000));

        Assert.That(plan, Is.Null);
    }

    [Test]
    public async Task IgnoresRulesForOtherWallets()
    {
        Configure(new SettlementConfig("other-wallet", Destination, 0));

        var plan = await CreatePolicy().EvaluateAsync(Context(250_000));

        Assert.That(plan, Is.Null);
    }

    [Test]
    public async Task DoesNotFire_OnZeroBalance()
    {
        Configure(new SettlementConfig(WalletId, Destination, 0));

        var plan = await CreatePolicy().EvaluateAsync(Context(0));

        Assert.That(plan, Is.Null);
    }

    [Test]
    public async Task PicksTheLowestMatchingThreshold()
    {
        var low = SettlementDestination.Ark("ark1qlow");
        var high = SettlementDestination.Ark("ark1qhigh");
        Configure(
            new SettlementConfig(WalletId, high, 200_000),
            new SettlementConfig(WalletId, low, 100_000));

        var plan = await CreatePolicy().EvaluateAsync(Context(250_000));

        Assert.That(plan!.Destination, Is.EqualTo(low));
    }
}

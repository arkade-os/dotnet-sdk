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
    private const string Usdt0 = "usdt0-asset-id";
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

    private static SettlementContext Context(
        long balanceSats,
        IReadOnlyDictionary<string, ulong>? assetBalances = null) =>
        new(WalletId, Array.Empty<ArkCoin>(), balanceSats, CurrentTime, Array.Empty<ArkCoin>(),
            assetBalances ?? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase));

    private async Task<List<SettlementPlan>> Evaluate(
        long balanceSats,
        IReadOnlyDictionary<string, ulong>? assetBalances = null)
    {
        var plans = new List<SettlementPlan>();
        await foreach (var plan in new BalanceThresholdSettlementPolicy(_configProvider)
                           .EvaluateAsync(Context(balanceSats, assetBalances)))
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
            Assert.That(plans[0].Amount, Is.EqualTo(250_000));
            Assert.That(plans[0].Destination, Is.EqualTo(Destination));
            Assert.That(plans[0].Coins, Is.Null);
        });
    }

    [Test]
    public async Task CapsAtMaxAmount()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000, MaxAmount: 150_000));

        var plans = await Evaluate(250_000);

        Assert.That(plans[0].Amount, Is.EqualTo(150_000));
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
    public async Task FiresOnAnAssetBalance_WhenTheRuleNamesThatAsset()
    {
        var assetDestination = SettlementDestination.ArkAsset("ark1qexample", Usdt0);
        Configure(new SettlementConfig(WalletId, assetDestination, 500_000, SourceAsset: Usdt0));

        var plans = await Evaluate(0, new Dictionary<string, ulong> { [Usdt0] = 1_250_000 });

        Assert.That(plans, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            // Asset amounts are atomic units of the asset, never satoshis.
            Assert.That(plans[0].Amount, Is.EqualTo(1_250_000));
            Assert.That(plans[0].SourceAsset, Is.EqualTo(Usdt0));
        });
    }

    [Test]
    public async Task DoesNotFire_WhenTheAssetBalanceIsBelowTheAssetThreshold()
    {
        Configure(new SettlementConfig(
            WalletId, SettlementDestination.ArkAsset("ark1qexample", Usdt0), 500_000, SourceAsset: Usdt0));

        // A satoshi balance far above the threshold number must not fire an asset rule.
        Assert.That(
            await Evaluate(5_000_000, new Dictionary<string, ulong> { [Usdt0] = 499_999 }),
            Is.Empty);
    }

    [Test]
    public async Task DoesNotFireABtcRule_OnAnAssetBalance()
    {
        Configure(new SettlementConfig(WalletId, Destination, 100_000));

        Assert.That(await Evaluate(0, new Dictionary<string, ulong> { [Usdt0] = 5_000_000 }), Is.Empty);
    }

    [Test]
    public async Task MeasuresBtcAndAssetRulesIndependently()
    {
        var assetDestination = SettlementDestination.ArkAsset("ark1qasset", Usdt0);
        Configure(
            new SettlementConfig(WalletId, Destination, 100_000),
            new SettlementConfig(WalletId, assetDestination, 500_000, SourceAsset: Usdt0));

        var plans = await Evaluate(250_000, new Dictionary<string, ulong> { [Usdt0] = 900_000 });

        Assert.That(plans, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            var btc = plans.Single(plan => plan.SourceAsset == SettlementAssets.Btc);
            var asset = plans.Single(plan => plan.SourceAsset == Usdt0);
            Assert.That(btc.Amount, Is.EqualTo(250_000));
            Assert.That(asset.Amount, Is.EqualTo(900_000));
        });
    }

    [Test]
    public async Task YieldsEveryMatchingRule_LowestThresholdFirst()
    {
        var low = SettlementDestination.Ark("ark1qlow");
        var high = SettlementDestination.Ark("ark1qhigh");
        Configure(
            new SettlementConfig(WalletId, high, 200_000, MaxAmount: 50_000),
            new SettlementConfig(WalletId, low, 100_000, MaxAmount: 50_000));

        var plans = await Evaluate(250_000);

        // Union semantics: both rules get a plan, and the engine executes them against a
        // shrinking balance rather than the policy picking a single winner.
        Assert.That(plans.Select(plan => plan.Destination), Is.EqualTo(new[] { low, high }));
    }
}

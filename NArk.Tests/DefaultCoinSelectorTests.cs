using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Core.CoinSelector;
using NArk.Core.Contracts;
using NBitcoin;

namespace NArk.Tests;

[TestFixture]
public class DefaultCoinSelectorTests
{
    private const string ServerHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string UserHex   = "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4";

    // Collaborative-path ArkPaymentContract: 430 WU per input.
    private const int CoinWu = 430;

    private NArk.Core.CoinSelector.DefaultCoinSelector _selector;

    [SetUp]
    public void SetUp()
    {
        _selector = new NArk.Core.CoinSelector.DefaultCoinSelector();
    }

    [Test]
    public void SelectsExactCoin_WhenAvailable()
    {
        var coins = new List<ArkCoin> { CreateCoin(5000) };
        var target = Money.Satoshis(5000);
        var dust = Money.Satoshis(546);

        var result = _selector.SelectCoins(coins, target, dust, currentSubDustOutputs: 0);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().Amount, Is.EqualTo(target));
    }

    [Test]
    public void SelectsGreedily_WithChangeAboveDust()
    {
        // Coins sorted by value descending (as the selector expects)
        var coins = new List<ArkCoin>
        {
            CreateCoin(8000),
            CreateCoin(5000),
            CreateCoin(3000)
        };
        var target = Money.Satoshis(7000);
        var dust = Money.Satoshis(546);

        var result = _selector.SelectCoins(coins, target, dust, currentSubDustOutputs: 0);

        // Greedy: picks 8000 first, change = 1000 > 546 dust, so it stops
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().Amount, Is.EqualTo(Money.Satoshis(8000)));
    }

    [Test]
    public void ThrowsNotEnoughFunds_WhenInsufficientBalance()
    {
        var coins = new List<ArkCoin>
        {
            CreateCoin(3000),
            CreateCoin(2000)
        };
        var target = Money.Satoshis(10000);
        var dust = Money.Satoshis(546);

        Assert.Throws<NotEnoughFundsException>(() =>
            _selector.SelectCoins(coins, target, dust, currentSubDustOutputs: 0));
    }

    [Test]
    public void ThrowsNotEnoughFunds_WhenNoCoins()
    {
        var coins = new List<ArkCoin>();
        var target = Money.Satoshis(1000);
        var dust = Money.Satoshis(546);

        Assert.Throws<NotEnoughFundsException>(() =>
            _selector.SelectCoins(coins, target, dust, currentSubDustOutputs: 0));
    }

    [Test]
    public void FindsBetterCombination_ToAvoidSubdustChange()
    {
        // Scenario: greedy picks 5000, change = 5000 - 4800 = 200 (subdust, below 546).
        // With currentSubDustOutputs at the max, we can't add another OP_RETURN.
        // Strategy 2: add next coin (3000): change = 200 + 3000 = 3200 >= 546.
        var coins = new List<ArkCoin>
        {
            CreateCoin(5000), // greedy picks this first, change = 200 (subdust)
            CreateCoin(3000), // greedy then picks this, total = 8000, change = 3200 (above dust)
            CreateCoin(2000),
        };
        var target = Money.Satoshis(4800);
        var dust = Money.Satoshis(546);

        var maxOpReturn = NArk.Core.Helpers.TransactionHelpers.MaxOpReturnOutputs;
        var result = _selector.SelectCoins(coins, target, dust, currentSubDustOutputs: maxOpReturn);

        var totalSelected = result.Sum(c => c.Amount);
        var change = totalSelected - target;
        Assert.That(change >= dust || change == Money.Zero,
            $"Change {change} should be zero or above dust threshold {dust}");
    }

    [Test]
    public void AllowsSubdustChange_WhenOpReturnSlotAvailable()
    {
        // When currentSubDustOutputs = 0 (below max of 1), subdust change is allowed.
        // Greedy picks 5000: change = 200 < 546, but we CAN add OP_RETURN.
        var coins = new List<ArkCoin>
        {
            CreateCoin(5000),
            CreateCoin(3000),
        };
        var target = Money.Satoshis(4800);
        var dust = Money.Satoshis(546);

        var result = _selector.SelectCoins(coins, target, dust, currentSubDustOutputs: 0);

        // Should pick just the 5000 coin since subdust change is allowed
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().Amount, Is.EqualTo(Money.Satoshis(5000)));
    }

    [Test]
    public void SelectsCoinsWithAsset_WhenAssetRequired()
    {
        var coins = new List<ArkCoin>
        {
            CreateCoinWithAssets(5000, [new VtxoAsset("asset_x", 100)]),
            CreateCoin(3000),
        };
        var requirements = new List<AssetRequirement> { new("asset_x", 50) };
        var result = _selector.SelectCoins(coins, Money.Satoshis(1000), requirements, Money.Satoshis(546), 0);

        // Asset coin has 5000 sats which covers 1000 BTC need
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().Assets, Is.Not.Null);
    }

    [Test]
    public void SelectsAdditionalBtcCoins_WhenAssetCoinsInsufficientBtc()
    {
        var coins = new List<ArkCoin>
        {
            CreateCoinWithAssets(500, [new VtxoAsset("asset_x", 100)]),
            CreateCoin(3000),
        };
        var requirements = new List<AssetRequirement> { new("asset_x", 50) };
        var result = _selector.SelectCoins(coins, Money.Satoshis(2000), requirements, Money.Satoshis(546), 0);

        // Should select both: coin A for asset, coin B for remaining BTC
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void ThrowsNotEnoughFunds_WhenAssetInsufficient()
    {
        var coins = new List<ArkCoin>
        {
            CreateCoinWithAssets(5000, [new VtxoAsset("asset_x", 30)]),
            CreateCoin(3000),
        };
        var requirements = new List<AssetRequirement> { new("asset_x", 50) };

        Assert.Throws<NotEnoughFundsException>(() =>
            _selector.SelectCoins(coins, Money.Satoshis(1000), requirements, Money.Satoshis(546), 0));
    }

    [Test]
    public void AssetSelection_MultipleAssets_SelectsBothAssetCoins()
    {
        var coins = new List<ArkCoin>
        {
            CreateCoinWithAssets(2000, [new VtxoAsset("asset_x", 100)]),
            CreateCoinWithAssets(3000, [new VtxoAsset("asset_y", 200)]),
            CreateCoin(4000),
        };
        var requirements = new List<AssetRequirement>
        {
            new("asset_x", 50),
            new("asset_y", 100),
        };
        var result = _selector.SelectCoins(coins, Money.Satoshis(1000), requirements, Money.Satoshis(546), 0);

        // Both asset coins (2000 + 3000 = 5000 sats) cover the 1000 BTC need
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void AssetSelection_EmptyRequirements_FallsBackToStandard()
    {
        var coins = new List<ArkCoin>
        {
            CreateCoin(5000),
        };
        var result = _selector.SelectCoins(coins, Money.Satoshis(3000), new List<AssetRequirement>(), Money.Satoshis(546), 0);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void RespectsWeightBudget_WhenTargetReachableWithinBudget()
    {
        var coins = new List<ArkCoin>
        {
            CreateCoin(5000),
            CreateCoin(3000),
            CreateCoin(2000),
        };

        // Budget for 2 inputs; 3rd would exceed it.
        var result = _selector.SelectCoins(coins, Money.Satoshis(7000), Money.Satoshis(546),
            currentSubDustOutputs: 0, maxInputWeightWu: 2 * CoinWu);

        Assert.That(result, Has.Count.LessThanOrEqualTo(2));
        Assert.That(result.Sum(c => c.Amount.Satoshi), Is.GreaterThanOrEqualTo(7000L));
    }

    [Test]
    public void ThrowsTooManyInputs_WhenTargetNeedsMoreWeightThanBudget()
    {
        // 10 x 1000 sats; covering 5000 needs 5 inputs (5 × 430 = 2150 WU) but budget is 4 × 430 = 1720 WU.
        var coins = Enumerable.Range(0, 10).Select(_ => CreateCoin(1000)).ToList();

        Assert.Throws<TooManyInputsException>(() =>
            _selector.SelectCoins(coins, Money.Satoshis(5000), Money.Satoshis(546),
                currentSubDustOutputs: 0, maxInputWeightWu: 4 * CoinWu));
    }

    [Test]
    public void ThrowsNotEnoughFunds_NotTooManyInputs_WhenBalanceInsufficient()
    {
        // A genuinely insufficient balance reports NotEnoughFunds even when a budget applies.
        var coins = new List<ArkCoin> { CreateCoin(1000), CreateCoin(1000) };

        Assert.Throws<NotEnoughFundsException>(() =>
            _selector.SelectCoins(coins, Money.Satoshis(5000), Money.Satoshis(546),
                currentSubDustOutputs: 0, maxInputWeightWu: 1 * CoinWu));
    }

    [Test]
    public void AssetSelection_RespectsWeightBudget_AcrossAssetAndBtcCoins()
    {
        // Budget for exactly 1 input (430 WU). The asset coin consumes the budget;
        // filling the remaining BTC target would need a second input → throws.
        var coins = new List<ArkCoin>
        {
            CreateCoinWithAssets(500, [new VtxoAsset("asset_x", 100)]),
            CreateCoin(3000),
        };
        var requirements = new List<AssetRequirement> { new("asset_x", 50) };

        Assert.Throws<TooManyInputsException>(() =>
            _selector.SelectCoins(coins, Money.Satoshis(2000), requirements, Money.Satoshis(546),
                currentSubDustOutputs: 0, maxInputWeightWu: 1 * CoinWu));
    }

    private static ArkPaymentContract MakeContract() =>
        new(
            KeyExtensions.ParseOutputDescriptor(ServerHex, Network.RegTest),
            new Sequence(144),
            KeyExtensions.ParseOutputDescriptor(UserHex, Network.RegTest));

    private static ArkCoin CreateCoinWithAssets(long satoshis, IReadOnlyList<VtxoAsset> assets)
    {
        var contract = MakeContract();
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(satoshis), contract.GetScriptPubKey());

        return new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: contract.CollaborativePath(),
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false,
            unrolled: false,
            assets: assets);
    }

    private static ArkCoin CreateCoin(long satoshis)
    {
        var contract = MakeContract();
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(satoshis), contract.GetScriptPubKey());

        return new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: contract.CollaborativePath(),
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false,
            unrolled: false);
    }
}

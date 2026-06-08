using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Core.CoinSelector.EARCoinSelector;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class ExpiryFirstStrategyTests
{
    private static readonly Money Dust = Money.Satoshis(546);
    private readonly ExpiryFirstStrategy _strategy = new();

    private static SelectionContext Ctx(long targetSats, bool allowSubDust = false) =>
        new(TargetAmount: Money.Satoshis(targetSats),
            DustThreshold: Dust,
            AllowExpiryMixing: false,
            AllowSubDust: allowSubDust,
            AllowDustInputs: true,
            MaxInputs: 100,
            CurrentSubDustOutputs: 0,
            MaxSubDustOutputs: 1,
            AssetRequirements: []);

    private static CoinSelectionPolicy Policy(bool allowMixingFallback = false) =>
        new(AllowExpiryMixingFallback: allowMixingFallback);

    [Test]
    public void SingleGroup_ExactMatch_ReturnsOneCoinNoChange()
    {
        var candidates = new[] { EarsTestHelpers.Candidate(5000, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsValid, Is.True);
        Assert.That(result.SelectedCoins, Has.Count.EqualTo(1));
        Assert.That(result.Change, Is.EqualTo(Money.Zero));
    }

    [Test]
    public void SingleGroup_PicksLargestCoinFirst()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(8000, expiry: 100u),
            EarsTestHelpers.Candidate(2000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(7000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SelectedCoins, Has.Count.EqualTo(1));
        Assert.That(result.SelectedCoins[0].TxOut.Value, Is.EqualTo(Money.Satoshis(8000)));
    }

    [Test]
    public void SingleGroup_AccumulatesMultipleCoins_WhenNeeded()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(2000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(4000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SelectedCoins, Has.Count.EqualTo(2));
        Assert.That(result.Change, Is.EqualTo(Money.Satoshis(1000)));
    }

    [Test]
    public void SingleGroup_RejectsSubDustChange_WhenNotAllowed()
    {
        // 5000 - 4700 = 300 < 546 dust
        var candidates = new[] { EarsTestHelpers.Candidate(5000, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(4700, allowSubDust: false), Policy());

        Assert.That(result, Is.Null);
    }

    [Test]
    public void SingleGroup_AcceptsSubDustChange_WhenAllowed()
    {
        var candidates = new[] { EarsTestHelpers.Candidate(5000, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(4700, allowSubDust: true), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsValid, Is.True);
    }

    [Test]
    public void MultipleGroups_PrefersEarliestExpiry()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(10000, expiry: 200u),
            EarsTestHelpers.Candidate(10000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(9000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ExpiryGroup, Is.EqualTo(100u));
        Assert.That(result.ExpiryMixedFallback, Is.False);
    }

    [Test]
    public void MultipleGroups_FallsToNextGroup_WhenFirstInsufficient()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(2000, expiry: 100u),
            EarsTestHelpers.Candidate(8000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ExpiryGroup, Is.EqualTo(200u));
        Assert.That(result.ExpiryMixedFallback, Is.False);
    }

    [Test]
    public void MixingFallback_ReturnsNull_WhenDisabledAndNoGroupSufficient()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(allowMixingFallback: false));

        Assert.That(result, Is.Null);
    }

    [Test]
    public void MixingFallback_CombinesGroups_WhenEnabled()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(allowMixingFallback: true));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsValid, Is.True);
        Assert.That(result.ExpiryMixedFallback, Is.True);
        Assert.That(result.TotalValue, Is.GreaterThanOrEqualTo(Money.Satoshis(5000)));
    }

    [Test]
    public void ReturnsNull_WhenTotalInsufficient()
    {
        var candidates = new[] { EarsTestHelpers.Candidate(1000, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Null);
    }

    [Test]
    public void RespectsMaxInputs_ReturnsNull_WhenLimitTooLow()
    {
        var candidates = Enumerable.Range(0, 20).Select(_ => EarsTestHelpers.Candidate(100, expiry: 100u)).ToList();
        var context = new SelectionContext(
            TargetAmount: Money.Satoshis(500),
            DustThreshold: Dust,
            AllowExpiryMixing: false,
            AllowSubDust: true,
            AllowDustInputs: true,
            MaxInputs: 3,
            CurrentSubDustOutputs: 0,
            MaxSubDustOutputs: 1,
            AssetRequirements: []);

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), context, Policy());

        // 3 coins × 100 = 300 < 500, cannot satisfy target within MaxInputs
        Assert.That(result, Is.Null);
    }
}

[TestFixture]
public class RgliStrategyTests
{
    private static readonly Money Dust = Money.Satoshis(546);
    private readonly RgliStrategy _strategy = new();

    private static SelectionContext Ctx(long targetSats, bool allowSubDust = false) =>
        new(TargetAmount: Money.Satoshis(targetSats),
            DustThreshold: Dust,
            AllowExpiryMixing: false,
            AllowSubDust: allowSubDust,
            AllowDustInputs: true,
            MaxInputs: 100,
            CurrentSubDustOutputs: 0,
            MaxSubDustOutputs: 1,
            AssetRequirements: []);

    private static CoinSelectionPolicy Policy(
        bool allowMixingFallback = false,
        int randomTopK = 10,
        int maxIterations = 50) =>
        new(AllowExpiryMixingFallback: allowMixingFallback,
            RandomTopK: randomTopK,
            MaxLocalSearchIterations: maxIterations);

    [Test]
    public void LocalImprovement_RemovesExcessCoin()
    {
        // Worst seed [2000,2500,3000] → greedy picks all 3 (change=2500).
        // Local improve: remove 2000 → [2500,3000] change=500, then swap 2500→2000 → [3000,2000] change=0.
        // Any seed converges to waste≤500 after improvement, so single TrySelect suffices.
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(2500, expiry: 100u),
            EarsTestHelpers.Candidate(2000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.TotalValue, Is.GreaterThanOrEqualTo(Money.Satoshis(5000)));
        Assert.That(result.Change, Is.EqualTo(Money.Zero).Or.GreaterThanOrEqualTo(Dust));
        Assert.That(result.Waste, Is.LessThanOrEqualTo(Money.Satoshis(500)));
    }

    [Test]
    public void LocalImprovement_FindsExactMatch_ViaSwap()
    {
        // Worst seed [1000,6000,5000] → greedy picks [1000+6000]=7000 (change=2000).
        // Local improve: remove 1000 → [6000] change=1000, swap 6000→5000 → [5000] change=0.
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(6000, expiry: 100u),
            EarsTestHelpers.Candidate(5000, expiry: 100u),
            EarsTestHelpers.Candidate(1000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Change, Is.EqualTo(Money.Zero));
        Assert.That(result.SelectedCoins, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReturnsValidResult_ChangeAboveDustOrZero()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(8000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(7000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsValid, Is.True);
        Assert.That(result.Change == Money.Zero || result.Change >= Dust, Is.True,
            $"Change {result.Change} must be zero or >= dust");
    }

    [Test]
    public void ReturnsNull_WhenInsufficientFunds()
    {
        var candidates = new[] { EarsTestHelpers.Candidate(1000, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Null);
    }

    [Test]
    public void RespectsExpiry_ReturnsNull_WhenMixingDisabledAndNoGroupSufficient()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(allowMixingFallback: false));

        Assert.That(result, Is.Null);
    }

    [Test]
    public void CombinesGroups_WhenMixingEnabled()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(allowMixingFallback: true));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsValid, Is.True);
        Assert.That(result.ExpiryMixedFallback, Is.True);
    }
}

[TestFixture]
public class BranchAndBoundStrategyTests
{
    private static readonly Money Dust = Money.Satoshis(546);
    private readonly BranchAndBoundStrategy _strategy = new();

    private static SelectionContext Ctx(long targetSats, bool allowSubDust = false) =>
        new(TargetAmount: Money.Satoshis(targetSats),
            DustThreshold: Dust,
            AllowExpiryMixing: false,
            AllowSubDust: allowSubDust,
            AllowDustInputs: true,
            MaxInputs: 100,
            CurrentSubDustOutputs: 0,
            MaxSubDustOutputs: 1,
            AssetRequirements: []);

    private static CoinSelectionPolicy Policy(int maxBnBInputs = 12) =>
        new(MaxBnBInputs: maxBnBInputs);

    [Test]
    public void FindsExactMatch_SingleCoin()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(5000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Change, Is.EqualTo(Money.Zero));
        Assert.That(result.SelectedCoins, Has.Count.EqualTo(1));
    }

    [Test]
    public void FindsExactMatch_CombinationOfCoins()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(2000, expiry: 100u),
            EarsTestHelpers.Candidate(1000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Change, Is.EqualTo(Money.Zero));
        Assert.That(result.TotalValue, Is.EqualTo(Money.Satoshis(5000)));
    }

    [Test]
    public void PrefersExactMatch_OverLowerWasteWithChange()
    {
        // BnB should find 3000+2000=5000 (change=0) rather than 6000 (change=1000)
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(6000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(2000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Change, Is.EqualTo(Money.Zero));
    }

    [Test]
    public void FindsMinimalWaste_WhenNoExactMatch()
    {
        // No combination sums to exactly 5000.
        // Options: 5800→change=800, 5600→change=600. BnB should pick 5600 (less waste).
        // Note: 5000+400=5400 would be sub-dust (400<546), so 5600 is the true minimum above dust.
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(5800, expiry: 100u),
            EarsTestHelpers.Candidate(5600, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Change, Is.EqualTo(Money.Satoshis(600)));
    }

    [Test]
    public void RejectsDustChange_WhenNotAllowed()
    {
        // Only solution: 5200 (change=200 < 546 dust) — should be rejected
        var candidates = new[] { EarsTestHelpers.Candidate(5200, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000, allowSubDust: false), Policy());

        Assert.That(result, Is.Null);
    }

    [Test]
    public void AcceptsDustChange_WhenAllowed()
    {
        var candidates = new[] { EarsTestHelpers.Candidate(5200, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000, allowSubDust: true), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Change, Is.EqualTo(Money.Satoshis(200)));
    }

    [Test]
    public void SkipsGroup_WhenExceedsMaxBnBInputs()
    {
        // Group has 5 coins but MaxBnBInputs=3 — should be skipped entirely
        var candidates = Enumerable.Range(0, 5)
            .Select(_ => EarsTestHelpers.Candidate(2000, expiry: 100u))
            .ToList();

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(maxBnBInputs: 3));

        Assert.That(result, Is.Null);
    }

    [Test]
    public void PrefersEarliestExpiryGroup_WhenBothHaveExactMatch()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(5000, expiry: 200u),
            EarsTestHelpers.Candidate(5000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ExpiryGroup, Is.EqualTo(100u));
    }

    [Test]
    public void ReturnsNull_WhenInsufficientFunds()
    {
        var candidates = new[] { EarsTestHelpers.Candidate(1000, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Null);
    }
}

[TestFixture]
public class SingleRandomDrawStrategyTests
{
    private static readonly Money Dust = Money.Satoshis(546);
    private readonly SingleRandomDrawStrategy _strategy = new();

    private static SelectionContext Ctx(long targetSats, bool allowSubDust = false) =>
        new(TargetAmount: Money.Satoshis(targetSats),
            DustThreshold: Dust,
            AllowExpiryMixing: false,
            AllowSubDust: allowSubDust,
            AllowDustInputs: true,
            MaxInputs: 100,
            CurrentSubDustOutputs: 0,
            MaxSubDustOutputs: 1,
            AssetRequirements: []);

    private static CoinSelectionPolicy Policy(bool allowMixingFallback = false) =>
        new(AllowExpiryMixingFallback: allowMixingFallback);

    [Test]
    public void ReturnsValidResult_CoveringTarget()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(5000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(4000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsValid, Is.True);
        Assert.That(result.TotalValue, Is.GreaterThanOrEqualTo(Money.Satoshis(4000)));
    }

    [Test]
    public void ChangeIsAboveDustOrZero()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(8000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 100u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(7000), Policy());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Change == Money.Zero || result.Change >= Dust, Is.True,
            $"Change {result.Change} must be zero or >= dust");
    }

    [Test]
    public void ReturnsNull_WhenInsufficientFunds()
    {
        var candidates = new[] { EarsTestHelpers.Candidate(1000, expiry: 100u) };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy());

        Assert.That(result, Is.Null);
    }

    [Test]
    public void RespectsExpiry_ReturnsNull_WhenMixingDisabledAndNoGroupSufficient()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(allowMixingFallback: false));

        Assert.That(result, Is.Null);
    }

    [Test]
    public void CombinesGroups_WhenMixingEnabled()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(3000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(allowMixingFallback: true));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsValid, Is.True);
        Assert.That(result.ExpiryMixedFallback, Is.True);
    }

    [Test]
    public void ExpiryMixedFallback_IsFalse_WhenSingleGroupSufficient()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(6000, expiry: 100u),
            EarsTestHelpers.Candidate(6000, expiry: 200u),
        };

        var result = _strategy.TrySelect(EarsTestHelpers.Buckets(candidates), Ctx(5000), Policy(allowMixingFallback: true));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ExpiryMixedFallback, Is.False);
    }
}

[TestFixture]
public class CoinSelectionEngineTests
{
    [Test]
    public void BuildBuckets_GroupsCloseExpiries_IntoSingleBucket()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(1000, expiry: 100u),
            EarsTestHelpers.Candidate(1000, expiry: 200u),  // 100 blocks apart — within 144
            EarsTestHelpers.Candidate(1000, expiry: 244u),  // 144 blocks from 100 — still within window
        };

        var buckets = CoinSelectionEngine.BuildBuckets(candidates, windowBlocks: 144);

        Assert.That(buckets, Has.Count.EqualTo(1));
        Assert.That(buckets[0].Coins, Has.Count.EqualTo(3));
        Assert.That(buckets[0].ExpiryGroup, Is.EqualTo(100u));
    }

    [Test]
    public void BuildBuckets_SplitsBatches_WhenGapExceedsWindow()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(1000, expiry: 100u),
            EarsTestHelpers.Candidate(1000, expiry: 245u),  // 145 blocks from 100 — outside window
        };

        var buckets = CoinSelectionEngine.BuildBuckets(candidates, windowBlocks: 144);

        Assert.That(buckets, Has.Count.EqualTo(2));
        Assert.That(buckets[0].ExpiryGroup, Is.EqualTo(100u));
        Assert.That(buckets[1].ExpiryGroup, Is.EqualTo(245u));
    }

    [Test]
    public void BuildBuckets_OrdersBuckets_ByEarliestExpiryAscending()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(1000, expiry: 500u),
            EarsTestHelpers.Candidate(1000, expiry: 100u),
            EarsTestHelpers.Candidate(1000, expiry: 1000u),
        };

        var buckets = CoinSelectionEngine.BuildBuckets(candidates, windowBlocks: 144);

        Assert.That(buckets.Select(b => b.ExpiryGroup), Is.Ordered.Ascending);
    }

    [Test]
    public void BuildBuckets_OrdersCoinsWithinBucket_ByValueDescending()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(1000, expiry: 100u),
            EarsTestHelpers.Candidate(5000, expiry: 100u),
            EarsTestHelpers.Candidate(3000, expiry: 100u),
        };

        var buckets = CoinSelectionEngine.BuildBuckets(candidates, windowBlocks: 144);

        Assert.That(buckets[0].Coins.Select(c => c.Value),
            Is.EqualTo(new[] { Money.Satoshis(5000), Money.Satoshis(3000), Money.Satoshis(1000) }));
    }

    [Test]
    public void BuildBuckets_NoExpiryCoins_GoLastAndDontCorruptWindowing()
    {
        // Bug: windowStart == 0 caused all coins to merge into one bucket.
        // No-expiry coin (ExpiryGroup=0) must be separated before windowing.
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(1000, expiry: 0u),   // no expiry — boarding UTXO
            EarsTestHelpers.Candidate(1000, expiry: 100u),
            EarsTestHelpers.Candidate(1000, expiry: 500u), // gap > 144 blocks from 100
        };

        var buckets = CoinSelectionEngine.BuildBuckets(candidates, windowBlocks: 144);

        // Expect 3 buckets: [100], [500], [0] — not one collapsed bucket
        Assert.That(buckets, Has.Count.EqualTo(3));
        Assert.That(buckets[0].ExpiryGroup, Is.EqualTo(100u));
        Assert.That(buckets[1].ExpiryGroup, Is.EqualTo(500u));
        Assert.That(buckets[2].ExpiryGroup, Is.EqualTo(0u)); // no-expiry last
    }

    [Test]
    public void BuildBuckets_OnlyNoExpiryCoins_FormsSingleBucket()
    {
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(1000, expiry: 0u),
            EarsTestHelpers.Candidate(2000, expiry: 0u),
        };

        var buckets = CoinSelectionEngine.BuildBuckets(candidates, windowBlocks: 144);

        Assert.That(buckets, Has.Count.EqualTo(1));
        Assert.That(buckets[0].ExpiryGroup, Is.EqualTo(0u));
        Assert.That(buckets[0].Coins, Has.Count.EqualTo(2));
    }

    [Test]
    public void BuildBuckets_MultipleBatches_WithGaps()
    {
        // Three separate batches: [100,120], [300,310], [600]
        var candidates = new[]
        {
            EarsTestHelpers.Candidate(1000, expiry: 100u),
            EarsTestHelpers.Candidate(1000, expiry: 120u),
            EarsTestHelpers.Candidate(1000, expiry: 300u),
            EarsTestHelpers.Candidate(1000, expiry: 310u),
            EarsTestHelpers.Candidate(1000, expiry: 600u),
        };

        var buckets = CoinSelectionEngine.BuildBuckets(candidates, windowBlocks: 144);

        Assert.That(buckets, Has.Count.EqualTo(3));
        Assert.That(buckets[0].Coins, Has.Count.EqualTo(2));
        Assert.That(buckets[1].Coins, Has.Count.EqualTo(2));
        Assert.That(buckets[2].Coins, Has.Count.EqualTo(1));
    }
}

internal static class EarsTestHelpers
{
    internal static IReadOnlyList<ExpiryBucket> Buckets(IEnumerable<CoinCandidate> candidates) =>
        candidates
            .GroupBy(c => c.ExpiryGroup)
            .OrderBy(g => g.Key)
            .Select(g => new ExpiryBucket(
                ExpiryGroup: g.Key,
                Coins: g.OrderByDescending(c => c.Value).ToList(),
                TotalValue: g.Sum(c => c.Value)))
            .ToList();

    internal static CoinCandidate Candidate(long satoshis, uint expiry)
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(satoshis), script);

        var scriptBuilder = Substitute.For<NArk.Abstractions.Scripts.ScriptBuilder>();
        scriptBuilder.BuildScript().Returns(Enumerable.Empty<Op>());
        scriptBuilder.Build().Returns(new TapScript(Script.Empty, TapLeafVersion.C0));

        var contract = Substitute.For<ArkContract>(
            NArk.Abstractions.Extensions.KeyExtensions.ParseOutputDescriptor(
                "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
                Network.RegTest));

        var coin = new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: null,
            expiresAtHeight: expiry,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: scriptBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false,
            unrolled: false);

        return new CoinCandidate(
            Coin: coin,
            Value: txOut.Value,
            ExpiryGroup: expiry,
            IsDustProne: txOut.Value < 546,
            Assets: [],
            Weight: script.Length);
    }
}

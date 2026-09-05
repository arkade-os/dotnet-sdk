using Microsoft.Extensions.Logging;
using NArk.Core;
using NArk.Core.Batches;
using NArk.Core.Models.Options;
using NBitcoin;

namespace NArk.Tests.Batches;

/// <summary>
/// The batch expiry is the timelock of the sweep leaf — the operator's only unilateral path out of
/// the batch output — and tree validation can only prove the tree matches whatever expiry was
/// supplied. These tests pin the bounds that make the value itself trustworthy.
/// </summary>
[TestFixture]
public class BatchExpiryPolicyTests
{
    private static BatchExpiryPolicy Mainnet => BatchExpiryPolicy.ForNetwork(Network.Main);
    private static BatchExpiryPolicy Regtest => BatchExpiryPolicy.ForNetwork(Network.RegTest);

    // 24h floor rounded down to BIP-68's 512s granularity: 168 * 512.
    private const long MainnetFloorSeconds = 86016;

    [Test]
    [TestCase(0, TestName = "Zero is not a relative timelock")]
    [TestCase(-1, TestName = "Negative is not a relative timelock")]
    [TestCase(1, TestName = "One block would let the operator sweep immediately")]
    [TestCase(511, TestName = "Largest block-typed value is still block-typed")]
    [TestCase(512, TestName = "Shortest seconds-typed value is far below the floor")]
    [TestCase(86015, TestName = "Encodes to 85504s, one granularity unit below the floor")]
    public void Mainnet_RejectsUnsafeExpiry(long declaredExpiry)
    {
        Assert.Throws<InvalidBatchExpiryException>(() => Mainnet.Validate(declaredExpiry));
    }

    [Test]
    [TestCase(MainnetFloorSeconds, MainnetFloorSeconds, TestName = "Exactly the floor")]
    [TestCase(86400, MainnetFloorSeconds, TestName = "A literal 24h encodes down to the floor")]
    [TestCase(86528, 86528, TestName = "One granularity unit above the floor")]
    [TestCase(604800, 604672, TestName = "A week, encoded down to 1181 * 512")]
    public void Mainnet_AcceptsExpiry_AndReturnsEncodedValue(long declaredExpiry, long expectedSeconds)
    {
        var encoded = Mainnet.Validate(declaredExpiry);

        Assert.Multiple(() =>
        {
            Assert.That(encoded.LockType, Is.EqualTo(SequenceLockType.Time));
            // The sweep leaf commits to the encoded value, not the declared one, so that is what
            // Validate must hand back.
            Assert.That(encoded.LockPeriod.TotalSeconds, Is.EqualTo(expectedSeconds));
        });
    }

    [Test]
    [TestCase(1)]
    [TestCase(9)]
    public void Regtest_RejectsBlockTypedExpiryBelowFloor(long declaredExpiry)
    {
        Assert.Throws<InvalidBatchExpiryException>(() => Regtest.Validate(declaredExpiry));
    }

    [Test]
    [TestCase(10, TestName = "Exactly the 10-block floor")]
    // 180 is what the regtest stack runs (ARKD_VTXO_TREE_EXPIRY=180); guards the E2E suite.
    [TestCase(180, TestName = "The regtest stack's configured expiry")]
    [TestCase(511, TestName = "Largest block-typed value")]
    public void Regtest_AcceptsBlockTypedExpiry(long declaredExpiry)
    {
        var encoded = Regtest.Validate(declaredExpiry);

        Assert.Multiple(() =>
        {
            Assert.That(encoded.LockType, Is.EqualTo(SequenceLockType.Height));
            Assert.That(encoded.LockHeight, Is.EqualTo(declaredExpiry));
        });
    }

    [Test]
    [TestCase(512, 512)]
    [TestCase(3600, 3584)]
    public void Regtest_AcceptsSecondsTypedExpiry(long declaredExpiry, long expectedSeconds)
    {
        var encoded = Regtest.Validate(declaredExpiry);

        Assert.That(encoded.LockPeriod.TotalSeconds, Is.EqualTo(expectedSeconds));
    }

    [Test]
    public void BlockTypedExpiry_IsRejectedOffRegtest()
    {
        // arkd only permits a block-typed VTXO tree expiry on regtest, so anywhere else it signals a
        // server that is either misconfigured or lying.
        foreach (var network in new[] { Network.Main, Network.TestNet })
        {
            Assert.Throws<InvalidBatchExpiryException>(
                () => BatchExpiryPolicy.ForNetwork(network).Validate(180),
                $"block-typed expiry should be rejected on {network}");
        }
    }

    [Test]
    public void ExpiryTooLargeToEncode_IsRejected()
    {
        // 65535 * 512 is the largest BIP-68 encodable relative timelock.
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => Mainnet.Validate(0xFFFF * 512L));
            Assert.Throws<InvalidBatchExpiryException>(() => Mainnet.Validate(0xFFFF * 512L + 1));
        });
    }

    [Test]
    public void NonEncodableExpiry_WarnsButDoesNotThrow()
    {
        // Truncation is not a theft vector — the encoded value is what the leaf hash commits to, so a
        // server that rounds differently just fails tree validation. But the warning turns that
        // otherwise opaque failure into a legible one.
        var logger = new RecordingLogger();

        Mainnet.Validate(86400, logger);

        Assert.That(logger.Warnings, Has.Count.EqualTo(1));
        Assert.That(logger.Warnings[0], Does.Contain("86400").And.Contain("86016"));
    }

    [Test]
    public void EncodableExpiry_DoesNotWarn()
    {
        var logger = new RecordingLogger();

        Mainnet.Validate(MainnetFloorSeconds, logger);

        Assert.That(logger.Warnings, Is.Empty);
    }

    [Test]
    public void Floors_CanBeLoweredButNotDisabled()
    {
        var lowered = BatchExpiryPolicy.ForNetwork(Network.Main,
            new BatchExpiryOptions { MinimumExpiry = TimeSpan.FromSeconds(1024) });
        Assert.That(lowered.Validate(1024).LockPeriod.TotalSeconds, Is.EqualTo(1024));

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BatchExpiryPolicy.ForNetwork(Network.Main,
                new BatchExpiryOptions { MinimumExpiry = TimeSpan.Zero }));
            Assert.Throws<ArgumentOutOfRangeException>(() => BatchExpiryPolicy.ForNetwork(Network.Main,
                new BatchExpiryOptions { MinimumExpiry = TimeSpan.FromSeconds(-1) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => BatchExpiryPolicy.ForNetwork(Network.RegTest,
                new BatchExpiryOptions { MinimumExpiryBlocks = 0 }));
        });
    }

    [Test]
    public void BlockTypedExpiry_CanBeOptedIntoOffRegtest()
    {
        var policy = BatchExpiryPolicy.ForNetwork(Network.Main, new BatchExpiryOptions
        {
            AllowBlockTypedExpiry = true,
            MinimumExpiryBlocks = 144
        });

        Assert.Multiple(() =>
        {
            Assert.That(policy.Validate(144).LockHeight, Is.EqualTo(144));
            Assert.Throws<InvalidBatchExpiryException>(() => policy.Validate(143));
        });
    }

    [Test]
    [TestCase(1, TestName = "A one-second floor")]
    [TestCase(300, TestName = "A five-minute floor")]
    [TestCase(511, TestName = "One second below a granularity unit")]
    public void SecondsFloorBelowOneGranularityUnit_IsRejected(int floorSeconds)
    {
        // The seconds floor is rounded down to a multiple of 512 before comparison, so anything
        // below 512 rounds to zero and would accept every seconds-typed expiry the server declares
        // — a floor that reads as "lowered" but behaves as "switched off".
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new BatchExpiryPolicy(false, TimeSpan.FromSeconds(floorSeconds), 144));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => BatchExpiryPolicy.ForNetwork(
                    Network.Main, new BatchExpiryOptions { MinimumExpiry = TimeSpan.FromSeconds(floorSeconds) }));
        });
    }

    [Test]
    public void SecondsFloorOfExactlyOneGranularityUnit_IsAccepted()
    {
        // 512s is the shortest floor that still compares against something, and is the regtest default.
        var policy = new BatchExpiryPolicy(
            false, TimeSpan.FromSeconds(BatchExpiryPolicy.SecondsGranularity), 144);

        Assert.Multiple(() =>
        {
            Assert.That(policy.Validate(BatchExpiryPolicy.SecondsGranularity).LockPeriod.TotalSeconds,
                Is.EqualTo(BatchExpiryPolicy.SecondsGranularity));
            Assert.That(BatchExpiryPolicy.ForNetwork(Network.RegTest).MinimumExpiry,
                Is.EqualTo(TimeSpan.FromSeconds(BatchExpiryPolicy.SecondsGranularity)));
        });
    }

    [Test]
    public void Defaults_AreNetworkSpecific()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Mainnet.AllowBlockTypedExpiry, Is.False);
            Assert.That(Mainnet.MinimumExpiry, Is.EqualTo(TimeSpan.FromHours(24)));
            Assert.That(Regtest.AllowBlockTypedExpiry, Is.True);
            Assert.That(Regtest.MinimumExpiryBlocks, Is.EqualTo(10));
        });
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}

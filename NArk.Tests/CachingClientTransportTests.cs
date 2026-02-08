using NArk.Abstractions.Intents;
using NArk.Core;
using NArk.Abstractions.Extensions;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NArk.Tests;

[TestFixture]
public class CachingClientTransportTests
{
    private IClientTransport _inner;
    private CachingClientTransport _cachingTransport;
    private ArkServerInfo _testServerInfo;

    [SetUp]
    public void SetUp()
    {
        _testServerInfo = CreateServerInfo();
        _inner = Substitute.For<IClientTransport>();

        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_testServerInfo));

        // Use a long cache expiry for most tests (5 minutes), with short fetch timeout
        _cachingTransport = new CachingClientTransport(
            _inner,
            logger: null,
            cacheExpiry: TimeSpan.FromMinutes(5),
            fetchTimeout: TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ReturnsFromCache_WhenCacheIsValid()
    {
        // First call populates the cache
        var first = await _cachingTransport.GetServerInfoAsync();
        // Second call should return cached value without hitting inner
        var second = await _cachingTransport.GetServerInfoAsync();

        Assert.That(second, Is.SameAs(first));
        // Inner should have been called only once
        await _inner.Received(1).GetServerInfoAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FetchesFromInner_WhenCacheExpired()
    {
        // Use a very short cache expiry
        var shortCacheTransport = new CachingClientTransport(
            _inner,
            logger: null,
            cacheExpiry: TimeSpan.FromMilliseconds(50),
            fetchTimeout: TimeSpan.FromSeconds(10));

        // First call populates the cache
        await shortCacheTransport.GetServerInfoAsync();

        // Wait for cache to expire
        await Task.Delay(100);

        // Second call should fetch again
        await shortCacheTransport.GetServerInfoAsync();

        // Inner should have been called twice
        await _inner.Received(2).GetServerInfoAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsStaleCacheOnError()
    {
        // First call populates the cache
        var original = await _cachingTransport.GetServerInfoAsync();

        // Expire the cache by using InvalidateServerInfoCache
        _cachingTransport.InvalidateServerInfoCache();

        // Make inner throw on next fetch
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Server unreachable"));

        // Populate fresh cache first, then invalidate
        // Actually, InvalidateServerInfoCache sets _cachedServerInfo to null.
        // We need to first populate, then set up the failure differently.

        // Reset: create a fresh transport, populate, then expire and make inner fail
        var inner2 = Substitute.For<IClientTransport>();
        inner2.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_testServerInfo));

        var transport = new CachingClientTransport(
            inner2,
            logger: null,
            cacheExpiry: TimeSpan.FromMilliseconds(50),
            fetchTimeout: TimeSpan.FromSeconds(10));

        // First call succeeds and caches
        var cached = await transport.GetServerInfoAsync();

        // Wait for cache to expire
        await Task.Delay(100);

        // Now make inner throw
        inner2.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Server unreachable"));

        // Should return stale cached value
        var stale = await transport.GetServerInfoAsync();
        Assert.That(stale, Is.SameAs(cached));
    }

    [Test]
    public async Task PassThroughMethods_CallInner_RegisterIntent()
    {
        var intent = CreateDummyIntent();

        _inner.RegisterIntent(Arg.Any<ArkIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("intent-id-123"));

        var result = await _cachingTransport.RegisterIntent(intent);

        Assert.That(result, Is.EqualTo("intent-id-123"));
        await _inner.Received(1).RegisterIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == intent.IntentTxId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PassThroughMethods_CallInner_DeleteIntent()
    {
        var intent = CreateDummyIntent();

        await _cachingTransport.DeleteIntent(intent);

        await _inner.Received(1).DeleteIntent(
            Arg.Is<ArkIntent>(i => i.IntentTxId == intent.IntentTxId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidateCache_ForcesRefetch()
    {
        // Populate the cache
        await _cachingTransport.GetServerInfoAsync();
        Assert.That(_cachingTransport.HasValidServerInfoCache, Is.True);

        // Invalidate
        _cachingTransport.InvalidateServerInfoCache();
        Assert.That(_cachingTransport.HasValidServerInfoCache, Is.False);

        // Next call should go to inner
        await _cachingTransport.GetServerInfoAsync();

        await _inner.Received(2).GetServerInfoAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HasValidServerInfoCache_ReflectsState()
    {
        // Initially no cache
        Assert.That(_cachingTransport.HasValidServerInfoCache, Is.False);

        // After fetching, cache is valid
        await _cachingTransport.GetServerInfoAsync();
        Assert.That(_cachingTransport.HasValidServerInfoCache, Is.True);

        // After invalidation, cache is invalid
        _cachingTransport.InvalidateServerInfoCache();
        Assert.That(_cachingTransport.HasValidServerInfoCache, Is.False);
    }

    [Test]
    public void ThrowsOnFirstFetch_WhenNoStaleCache()
    {
        var failingInner = Substitute.For<IClientTransport>();
        failingInner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Cannot connect"));

        var transport = new CachingClientTransport(
            failingInner,
            logger: null,
            cacheExpiry: TimeSpan.FromMinutes(5),
            fetchTimeout: TimeSpan.FromSeconds(10));

        // No stale cache exists, so the exception should propagate
        Assert.ThrowsAsync<Exception>(async () =>
            await transport.GetServerInfoAsync());
    }

    private static ArkIntent CreateDummyIntent()
    {
        return new ArkIntent(
            IntentTxId: "test-intent-tx-id",
            IntentId: null,
            WalletId: "test-wallet",
            State: ArkIntentState.WaitingToSubmit,
            ValidFrom: DateTimeOffset.UtcNow.AddHours(-1),
            ValidUntil: DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RegisterProof: "dummy",
            RegisterProofMessage: "dummy",
            DeleteProof: "dummy",
            DeleteProofMessage: "dummy",
            BatchId: null,
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [],
            SignerDescriptor: "dummy-signer");
    }

    private static ArkServerInfo CreateServerInfo()
    {
        var serverKey = KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

        var emptyMultisig = new NArk.Core.Scripts.NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());

        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: serverKey,
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new NArk.Core.Scripts.UnilateralPathArkTapScript(
                new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"));
    }
}

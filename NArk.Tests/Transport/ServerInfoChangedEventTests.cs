using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NArk.Tests.Transport;

[TestFixture]
public class ServerInfoChangedEventTests
{
    [Test]
    public void InvalidateServerInfoCache_clears_cache_and_raises_event_once()
    {
        var inner = Substitute.For<IClientTransport>();
        var sut = new CachingClientTransport(inner, null);
        var invalidation = (IServerInfoCacheInvalidation)sut;

        var fired = 0;
        invalidation.ServerInfoChanged += (_, _) => fired++;
        invalidation.InvalidateServerInfoCache();

        Assert.That(fired, Is.EqualTo(1));
        Assert.That(sut.HasValidServerInfoCache, Is.False);
    }

    [Test]
    public void InvalidateServerInfoCache_with_reason_raises_event_with_correct_args()
    {
        var inner = Substitute.For<IClientTransport>();
        var sut = new CachingClientTransport(inner, null);
        var invalidation = (IServerInfoCacheInvalidation)sut;

        ServerInfoChangedEventArgs? capturedArgs = null;
        invalidation.ServerInfoChanged += (_, args) => capturedArgs = args;

        var explicitArgs = new ServerInfoChangedEventArgs
        {
            Reason = ServerInfoChangedReason.DigestMismatch,
            NewDigest = "newdigest"
        };
        invalidation.InvalidateServerInfoCache(explicitArgs);

        Assert.That(capturedArgs, Is.Not.Null);
        Assert.That(capturedArgs!.Reason, Is.EqualTo(ServerInfoChangedReason.DigestMismatch));
        Assert.That(capturedArgs.NewDigest, Is.EqualTo("newdigest"));
    }

    [Test]
    public void InvalidateServerInfoCache_parameterless_defaults_to_ManualInvalidation()
    {
        var inner = Substitute.For<IClientTransport>();
        var sut = new CachingClientTransport(inner, null);
        var invalidation = (IServerInfoCacheInvalidation)sut;

        ServerInfoChangedEventArgs? capturedArgs = null;
        invalidation.ServerInfoChanged += (_, args) => capturedArgs = args;
        invalidation.InvalidateServerInfoCache();

        Assert.That(capturedArgs!.Reason, Is.EqualTo(ServerInfoChangedReason.ManualInvalidation));
    }

    [Test]
    public async Task DigestMismatch_on_pass_through_raises_event_with_DigestMismatch_reason()
    {
        var inner = Substitute.For<IClientTransport>();
        var sut = new CachingClientTransport(inner, null);
        var invalidation = (IServerInfoCacheInvalidation)sut;

        ServerInfoChangedEventArgs? capturedArgs = null;
        invalidation.ServerInfoChanged += (_, args) => capturedArgs = args;

        inner.RegisterIntent(Arg.Any<NArk.Abstractions.Intents.ArkIntent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DigestMismatchException("mismatch"));

        Assert.ThrowsAsync<DigestMismatchException>(async () =>
            await sut.RegisterIntent(new NArk.Abstractions.Intents.ArkIntent(
                IntentTxId: "tx", IntentId: null, WalletId: "w",
                State: NArk.Abstractions.Intents.ArkIntentState.WaitingToSubmit,
                ValidFrom: DateTimeOffset.UtcNow, ValidUntil: DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
                RegisterProof: "p", RegisterProofMessage: "m",
                DeleteProof: "p", DeleteProofMessage: "m",
                BatchId: null, CommitmentTransactionId: null, CancellationReason: null,
                IntentVtxos: [], SignerDescriptor: "s")));

        Assert.That(capturedArgs, Is.Not.Null);
        Assert.That(capturedArgs!.Reason, Is.EqualTo(ServerInfoChangedReason.DigestMismatch));
    }

    [Test]
    public async Task TtlRefresh_with_changed_digest_raises_TtlExpiry_once()
    {
        var inner = Substitute.For<IClientTransport>();
        inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(ServerInfo("A")),
                Task.FromResult(ServerInfo("B")));

        // Short TTL so the second call goes through the refresh path.
        var sut = new CachingClientTransport(inner, null, cacheExpiry: TimeSpan.FromMilliseconds(20));
        var invalidation = (IServerInfoCacheInvalidation)sut;

        var captured = new List<ServerInfoChangedEventArgs>();
        invalidation.ServerInfoChanged += (_, args) => captured.Add(args);

        var first = await sut.GetServerInfoAsync();
        Assert.That(first.Digest, Is.EqualTo("A"));

        await Task.Delay(50); // let the cache expire

        var second = await sut.GetServerInfoAsync();
        Assert.That(second.Digest, Is.EqualTo("B"));

        Assert.That(captured, Has.Count.EqualTo(1), "exactly one ServerInfoChanged on a TTL-refresh digest change");
        Assert.That(captured[0].Reason, Is.EqualTo(ServerInfoChangedReason.TtlExpiry));
        Assert.That(captured[0].PreviousDigest, Is.EqualTo("A"));
        Assert.That(captured[0].NewDigest, Is.EqualTo("B"));
        // A normal refresh must NOT invalidate — the fresh value is cached and valid.
        Assert.That(sut.HasValidServerInfoCache, Is.True);
    }

    [Test]
    public async Task TtlRefresh_with_same_digest_does_not_raise()
    {
        var inner = Substitute.For<IClientTransport>();
        inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(ServerInfo("A")),
                Task.FromResult(ServerInfo("A")));

        var sut = new CachingClientTransport(inner, null, cacheExpiry: TimeSpan.FromMilliseconds(20));
        var invalidation = (IServerInfoCacheInvalidation)sut;

        var fired = 0;
        invalidation.ServerInfoChanged += (_, _) => fired++;

        await sut.GetServerInfoAsync();
        await Task.Delay(50);
        await sut.GetServerInfoAsync();

        Assert.That(fired, Is.EqualTo(0), "unchanged digest on refresh must not raise");
    }

    private static ArkServerInfo ServerInfo(string digest)
    {
        var serverKey = KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);
        var emptyMultisig = new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());
        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: serverKey,
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: digest);
    }
}

using NArk.Core;
using NArk.Core.Transport;
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
}

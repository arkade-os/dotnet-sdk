using System.Net;
using System.Text;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NArk.Transport;
using NArk.Transport.RestClient;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NArk.Tests;

[TestFixture]
public class DigestHolderTests
{
    [Test]
    public void InitiallyNull()
    {
        var holder = new DigestHolder();
        Assert.That(holder.Digest, Is.Null);
    }

    [Test]
    public void Set_CanBeReadBack()
    {
        var holder = new DigestHolder();
        holder.Digest = "abc123";
        Assert.That(holder.Digest, Is.EqualTo("abc123"));
    }

    [Test]
    public void Clear_SetsToNull()
    {
        var holder = new DigestHolder();
        holder.Digest = "abc123";
        holder.Clear();
        Assert.That(holder.Digest, Is.Null);
    }
}

[TestFixture]
public class BuildVersionHandlerDigestTests
{
    private DigestHolder _holder;
    private List<HttpRequestMessage> _captured;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _holder = new DigestHolder();
        _captured = new List<HttpRequestMessage>();
        _client = MakeClient(HttpStatusCode.OK, "{}");
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    private HttpClient MakeClient(HttpStatusCode status, string body)
    {
        var inner = new StubHandler(status, body, _captured);
        var handler = new BuildVersionHandler(_holder) { InnerHandler = inner };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    [Test]
    public async Task NoDigest_DoesNotAddXDigestHeader()
    {
        await _client.GetAsync("/v1/info");

        Assert.That(_captured[0].Headers.Contains(ArkdVersion.DigestHeaderName), Is.False);
    }

    [Test]
    public async Task WithDigest_AddsXDigestHeader()
    {
        _holder.Digest = "deadbeef";

        await _client.GetAsync("/v1/info");

        Assert.That(
            _captured[0].Headers.GetValues(ArkdVersion.DigestHeaderName),
            Contains.Item("deadbeef"));
    }

    [Test]
    public async Task DigestMismatchResponse_ThrowsAndClearsHolder()
    {
        _holder.Digest = "old-digest";
        var client = MakeClient(HttpStatusCode.BadRequest, "DIGEST_MISMATCH: server config changed");

        Assert.ThrowsAsync<DigestMismatchException>(async () => await client.GetAsync("/v1/info"));
        Assert.That(_holder.Digest, Is.Null);
    }

    [Test]
    public async Task DigestMismatchResponse_IsCaseInsensitive()
    {
        _holder.Digest = "old-digest";
        var client = MakeClient(HttpStatusCode.BadRequest, "digest_mismatch");

        Assert.ThrowsAsync<DigestMismatchException>(async () => await client.GetAsync("/v1/info"));
        Assert.That(_holder.Digest, Is.Null);
    }

    [Test]
    public void BuildVersionTooOldResponse_ThrowsIncompatibleSdkVersionException()
    {
        var client = MakeClient(HttpStatusCode.BadRequest, "BUILD_VERSION_TOO_OLD");

        Assert.ThrowsAsync<IncompatibleSdkVersionException>(async () => await client.GetAsync("/v1/info"));
    }

    [Test]
    public async Task OtherErrorResponse_DoesNotThrow_BodyStillReadable()
    {
        var client = MakeClient(HttpStatusCode.BadRequest, "{\"code\":\"SOME_OTHER_ERROR\"}");

        var response = await client.GetAsync("/v1/info");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body, Does.Contain("SOME_OTHER_ERROR"));
    }

    [Test]
    public async Task SuccessResponse_PassesThroughWithoutReadingBody()
    {
        _holder.Digest = "current";
        var client = MakeClient(HttpStatusCode.OK, "{\"dust\":546}");

        var response = await client.GetAsync("/v1/info");

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(_holder.Digest, Is.EqualTo("current"));
    }

    private sealed class StubHandler(HttpStatusCode status, string body, List<HttpRequestMessage> captured)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            captured.Add(request);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}

[TestFixture]
public class CachingTransportDigestMismatchTests
{
    private IClientTransport _inner;
    private CachingClientTransport _transport;

    [SetUp]
    public void SetUp()
    {
        _inner = Substitute.For<IClientTransport>();
        _transport = new CachingClientTransport(_inner, logger: null);
    }

    [Test]
    public async Task DigestMismatch_OnGetServerInfo_InvalidatesCacheAndRethrows()
    {
        // Populate cache with a successful first fetch
        var info = FakeServerInfo();
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(info));
        await _transport.GetServerInfoAsync();
        Assert.That(_transport.HasValidServerInfoCache, Is.True);

        // Next fetch throws DigestMismatchException
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DigestMismatchException("mismatch"));
        _transport.InvalidateServerInfoCache();

        Assert.ThrowsAsync<DigestMismatchException>(async () => await _transport.GetServerInfoAsync());
        Assert.That(_transport.HasValidServerInfoCache, Is.False);
    }

    [Test]
    public async Task DigestMismatch_OnGetServerInfo_DoesNotReturnStaleCache()
    {
        // Populate cache
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FakeServerInfo()));
        await _transport.GetServerInfoAsync();

        // Expire + make inner throw DigestMismatchException
        var shortTransport = new CachingClientTransport(
            _inner, logger: null,
            cacheExpiry: TimeSpan.FromMilliseconds(1),
            fetchTimeout: TimeSpan.FromSeconds(10));

        await shortTransport.GetServerInfoAsync();
        await Task.Delay(20);

        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DigestMismatchException("mismatch"));

        // Unlike a generic error (which returns stale cache), DigestMismatchException must propagate
        Assert.ThrowsAsync<DigestMismatchException>(async () => await shortTransport.GetServerInfoAsync());
    }

    [Test]
    public async Task DigestMismatch_OnPassThrough_InvalidatesCacheAndRethrows()
    {
        // Populate cache
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FakeServerInfo()));
        await _transport.GetServerInfoAsync();
        Assert.That(_transport.HasValidServerInfoCache, Is.True);

        _inner.RegisterIntent(Arg.Any<ArkIntent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DigestMismatchException("mismatch"));

        Assert.ThrowsAsync<DigestMismatchException>(async () =>
            await _transport.RegisterIntent(new ArkIntent(
                IntentTxId: "tx", IntentId: null, WalletId: "w",
                State: ArkIntentState.WaitingToSubmit,
                ValidFrom: DateTimeOffset.UtcNow, ValidUntil: DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
                RegisterProof: "p", RegisterProofMessage: "m",
                DeleteProof: "p", DeleteProofMessage: "m",
                BatchId: null, CommitmentTransactionId: null, CancellationReason: null,
                IntentVtxos: [], SignerDescriptor: "s")));

        Assert.That(_transport.HasValidServerInfoCache, Is.False);
    }

    [Test]
    public async Task DigestMismatch_OnStream_InvalidatesCacheAndRethrows()
    {
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FakeServerInfo()));
        await _transport.GetServerInfoAsync();
        Assert.That(_transport.HasValidServerInfoCache, Is.True);

        _inner.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(ThrowingStream());

        Assert.ThrowsAsync<DigestMismatchException>(async () =>
        {
            await foreach (var _ in _transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { "s" })) { }
        });

        Assert.That(_transport.HasValidServerInfoCache, Is.False);
    }

    [Test]
    public async Task AfterDigestMismatch_NextGetServerInfo_RefetchesFromInner()
    {
        var info = FakeServerInfo();
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(info));
        await _transport.GetServerInfoAsync();

        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DigestMismatchException("mismatch"));
        _transport.InvalidateServerInfoCache();
        try { await _transport.GetServerInfoAsync(); } catch (DigestMismatchException) { }

        // Restore inner to succeed again
        _inner.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(info));
        var result = await _transport.GetServerInfoAsync();

        Assert.That(result, Is.SameAs(info));
        await _inner.Received(3).GetServerInfoAsync(Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<ArkVtxo> ThrowingStream()
    {
        await Task.Yield();
        throw new DigestMismatchException("mismatch");
        yield break;
    }

    private static ArkServerInfo FakeServerInfo() => new(
        Dust: Money.Satoshis(546),
        SignerKey: KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest),
        DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
        Network: Network.RegTest,
        UnilateralExit: new Sequence(144),
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create(
            "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
        CheckpointTapScript: new UnilateralPathArkTapScript(
            new Sequence(144),
            new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>())),
        FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
        Digest: "server-digest-abc");
}

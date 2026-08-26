using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Common;
using NArk.Swaps.Models;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class BoltzClientBulkStatusTests
{
    [Test]
    public async Task GetsMultipleStatusesInOneRequest()
    {
        HttpRequestMessage? captured = null;
        var client = BoltzTestFixture.CreateClient(new BoltzTestFixture.StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"swap-a":{"status":"invoice.pending"},"swap-b":{"status":"transaction.claimed"}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        var statuses = await client.GetSwapStatusesAsync(["swap-a", "swap-b"]);

        Assert.Multiple(() =>
        {
            Assert.That(captured?.RequestUri?.AbsolutePath, Is.EqualTo("/v2/swap/status"));
            Assert.That(captured?.RequestUri?.Query, Is.EqualTo("?ids=swap-a&ids=swap-b"));
            Assert.That(statuses["swap-a"].Status, Is.EqualTo("invoice.pending"));
            Assert.That(statuses["swap-b"].Status, Is.EqualTo("transaction.claimed"));
        });
    }

    [Test]
    public void RejectsMoreThanMaxBatchSizeIds()
    {
        var client = BoltzTestFixture.CreateClient();
        var ids = Enumerable.Range(0, BoltzClient.MaxSwapStatusBatchSize + 1)
            .Select(i => $"swap-{i}").ToArray();

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await client.GetSwapStatusesAsync(ids));
    }

    [Test]
    public async Task ReconciliationChunksActiveSwapsAtMaxBatchSize()
    {
        var client = new RecordingBoltzClient(BoltzTestFixture.Options);
        var swaps = Enumerable.Range(0, BoltzClient.MaxSwapStatusBatchSize + 1)
            .Select(i => new ArkSwap(
                $"swap-{i}", "wallet", ArkSwapType.Submarine, "invoice", 1_000,
                $"script-{i}", "address", ArkSwapStatus.Pending, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash"))
            .ToArray();
        var storage = Substitute.For<ISwapStorage>();
        storage.GetSwaps(
                Arg.Any<string[]?>(), Arg.Any<string[]?>(), true,
                Arg.Any<ArkSwapType[]?>(), Arg.Any<ArkSwapStatus[]?>(),
                Arg.Any<string[]?>(), Arg.Any<string[]?>(), Arg.Any<string[]?>(),
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkSwap>>(swaps));

        var provider = BoltzTestFixture.CreateProvider(boltzClient: client, swapStorage: storage);

        await provider.ReconcileActiveSwaps(CancellationToken.None);

        Assert.Multiple(() =>
        {
            // The trailing one-element chunk deliberately does not go to the batch
            // endpoint: Boltz reads `ids` as an array only when the query repeats it,
            // so `?ids=x` is rejected with 400 "ids must be an array".
            Assert.That(client.BatchSizes,
                Is.EqualTo(new[] { BoltzClient.MaxSwapStatusBatchSize }));
            Assert.That(client.SinglePolls,
                Is.EqualTo(new[] { $"swap-{BoltzClient.MaxSwapStatusBatchSize}" }));
        });
    }

    private sealed class RecordingBoltzClient(IOptions<BoltzClientOptions> options)
        : BoltzClient(new HttpClient(new BoltzTestFixture.StubHandler()), options)
    {
        public List<int> BatchSizes { get; } = [];
        public List<string> SinglePolls { get; } = [];

        public override Task<IReadOnlyDictionary<string, SwapStatusResponse>> GetSwapStatusesAsync(
            IReadOnlyCollection<string> swapIds,
            CancellationToken cancellation = default)
        {
            BatchSizes.Add(swapIds.Count);
            // Answer for every requested ID: an incomplete batch response is its own
            // failure mode, re-polled individually, and would drown out the chunking
            // this test is about.
            return Task.FromResult<IReadOnlyDictionary<string, SwapStatusResponse>>(
                swapIds.ToDictionary(id => id, _ => new SwapStatusResponse { Status = "invoice.pending" }));
        }

        public override Task<SwapStatusResponse?> GetSwapStatusAsync(
            string swapId, CancellationToken cancellation)
        {
            SinglePolls.Add(swapId);
            return Task.FromResult<SwapStatusResponse?>(null);
        }
    }
}

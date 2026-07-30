using System.Net;
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
                    """{"swap-a":{"status":"invoice.pending"},"swap-b":{"status":"transaction.claimed"}}"""),
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

        Assert.That(client.BatchSizes,
            Is.EqualTo(new[] { BoltzClient.MaxSwapStatusBatchSize, 1 }));
    }

    private sealed class RecordingBoltzClient(IOptions<BoltzClientOptions> options)
        : BoltzClient(new HttpClient(new BoltzTestFixture.StubHandler()), options)
    {
        public List<int> BatchSizes { get; } = [];

        public override Task<IReadOnlyDictionary<string, SwapStatusResponse>> GetSwapStatusesAsync(
            IReadOnlyCollection<string> swapIds,
            CancellationToken cancellation = default)
        {
            BatchSizes.Add(swapIds.Count);
            return Task.FromResult<IReadOnlyDictionary<string, SwapStatusResponse>>(
                new Dictionary<string, SwapStatusResponse>());
        }
    }
}

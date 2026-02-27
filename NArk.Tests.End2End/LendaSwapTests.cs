using Microsoft.Extensions.Options;
using NArk.Swaps.Abstractions;
using NArk.Swaps.LendaSwap;
using NArk.Swaps.LendaSwap.Client;
using NArk.Tests.End2End.TestPersistance;

namespace NArk.Tests.End2End.Swaps;

[TestFixture]
public class LendaSwapTests
{
    /// <summary>
    /// LendaSwap regtest endpoint. Override via LENDASWAP_URL env var.
    /// </summary>
    private static readonly Uri LendaSwapEndpoint = new(
        Environment.GetEnvironmentVariable("LENDASWAP_URL") ?? "http://localhost:9080");

    private static LendaSwapClient CreateClient()
    {
        var httpClient = new HttpClient { BaseAddress = LendaSwapEndpoint };
        var options = Options.Create(new LendaSwapOptions
        {
            ApiUrl = LendaSwapEndpoint.ToString()
        });
        return new LendaSwapClient(httpClient, options);
    }

    private static bool IsLendaSwapAvailable()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.GetAsync($"{LendaSwapEndpoint}/tokens").GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Test]
    public async Task CanGetTokensFromLendaSwap()
    {
        if (!IsLendaSwapAvailable())
            Assert.Inconclusive($"LendaSwap server not available at {LendaSwapEndpoint}");

        var client = CreateClient();
        var tokens = await client.GetTokensAsync();

        Assert.That(tokens.BtcTokens, Is.Not.Null);
        Assert.That(tokens.EvmTokens, Is.Not.Null);
    }

    [Test]
    public async Task CanGetQuoteFromLendaSwap()
    {
        if (!IsLendaSwapAvailable())
            Assert.Inconclusive($"LendaSwap server not available at {LendaSwapEndpoint}");

        var client = CreateClient();
        var quote = await client.GetQuoteAsync(
            sourceChain: "Bitcoin",
            sourceToken: "btc",
            targetChain: "Arkade",
            targetToken: "btc",
            sourceAmount: 100_000);

        Assert.That(quote.SourceAmount, Is.GreaterThan(0));
        Assert.That(quote.TargetAmount, Is.GreaterThan(0));
        Assert.That(quote.MinAmount, Is.GreaterThan(0));
    }

    [Test]
    public async Task LendaSwapProvider_SupportsExpectedRoutes()
    {
        // This test doesn't need the server — tests route declarations
        var client = CreateClient();
        var provider = new LendaSwapProvider(client, new InMemorySwapStorage());

        Assert.Multiple(() =>
        {
            // BTC → Ark
            Assert.That(provider.SupportsRoute(
                new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc)), Is.True);

            // Ark → Polygon USDC
            Assert.That(provider.SupportsRoute(
                new SwapRoute(SwapAsset.ArkBtc,
                    SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0x3c499c"))), Is.True);

            // Ark → Lightning (not supported by LendaSwap)
            Assert.That(provider.SupportsRoute(
                new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning)), Is.False);
        });
    }
}

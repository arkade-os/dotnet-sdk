using System.Net.Http.Json;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;

namespace NArk.Tests.End2End.Swaps;

[SetUpFixture]
public class SharedSwapInfrastructure
{
    public static readonly Uri BoltzEndpoint = new("http://localhost:9069");
    public static readonly Uri BoltzWsEndpoint = new("ws://localhost:9004");
    public static readonly Uri FulmineEndpoint = new("http://localhost:7003");

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        ThreadPool.SetMinThreads(50, 50);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Health-check arkd + boltz (don't require 2xx — just verify reachable)
        foreach (var (name, url) in new[]
                 {
                     ("arkd", $"{SharedArkInfrastructure.ArkdEndpoint}/v1/info"),
                     ("boltz", $"{BoltzEndpoint}/version")
                 })
        {
            try
            {
                await http.GetAsync(url);
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"{name} not running. Start infrastructure with:\n" +
                    "  ./arkade-regtest/start-env.sh\n" +
                    "  (Windows: wsl bash ./arkade-regtest/start-env.sh)\n\n" +
                    $"Health check failed: {ex.Message}");
            }
        }

        // Wait for Boltz to have LND connectivity and ARK/BTC pairs loaded.
        // Boltz starts before boltz-lnd is ready, returning "BTC has no lightning support"
        // until the LND backend connects.
        await WaitForBoltzPairs(http);

        // Mine blocks to confirm any pending txs and mature coinbase outputs
        for (var i = 0; i < 6; i++)
            await DockerHelper.MineBlocks();

        // Ensure Fulmine has enough ARK VTXOs for all swap tests
        await FulmineLiquidityHelper.EnsureArkLiquidity();
    }

    private static async Task WaitForBoltzPairs(HttpClient http)
    {
        var maxAttempts = 60;
        string lastResponse = "";
        for (var i = 1; i <= maxAttempts; i++)
        {
            try
            {
                var response = await http.GetAsync($"{BoltzEndpoint}/v2/swap/submarine");
                lastResponse = await response.Content.ReadAsStringAsync();
                var pairs = System.Text.Json.JsonSerializer.Deserialize<SubmarinePairsResponse>(lastResponse);
                if (pairs?.ARK?.BTC != null)
                {
                    TestContext.Progress.WriteLine($"Boltz ARK/BTC pairs ready (attempt {i})");
                    return;
                }
            }
            catch (Exception ex)
            {
                lastResponse = $"Exception: {ex.Message} | Response: {lastResponse}";
            }

            if (i % 10 == 1 || i == maxAttempts)
                TestContext.Progress.WriteLine($"Waiting for Boltz LND connectivity... (attempt {i}/{maxAttempts}): {lastResponse}");

            if (i < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.Fail(
            $"Boltz did not report ARK/BTC pairs within 2 minutes. " +
            $"Last response: {lastResponse}");
    }
}

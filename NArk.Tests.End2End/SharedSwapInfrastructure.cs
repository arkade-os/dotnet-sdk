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
                    "  cd NArk.Tests.End2End/Infrastructure && ./start-env.sh\n" +
                    "  (Windows: wsl bash ./start-env.sh)\n\n" +
                    $"Health check failed: {ex.Message}");
            }
        }

        // Mine blocks to confirm any pending txs and mature coinbase outputs
        for (var i = 0; i < 6; i++)
            await DockerHelper.MineBlocks();

        // Ensure Fulmine has enough ARK VTXOs for all swap tests
        await FulmineLiquidityHelper.EnsureArkLiquidity();
    }
}

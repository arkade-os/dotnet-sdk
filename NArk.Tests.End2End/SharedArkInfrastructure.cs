namespace NArk.Tests.End2End.Core;

[SetUpFixture]
public class SharedArkInfrastructure
{
    public static readonly Uri ArkdEndpoint = new("http://localhost:7070");
    public static readonly Uri NbxplorerEndpoint = new("http://localhost:32838");
    // Esplora REST API served by the mempool container under /api.
    public static readonly Uri EsploraEndpoint = new("http://localhost:3000/api");

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        ThreadPool.SetMinThreads(50, 50);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            // arkd doesn't have a /health endpoint; just verify it's reachable
            await http.GetAsync($"{ArkdEndpoint}/v1/info");
        }
        catch (Exception ex)
        {
            Assert.Fail(
                "Ark infrastructure not running. Start it with:\n" +
                "  node regtest/regtest.mjs start\n\n" +
                $"Health check failed: {ex.Message}");
        }
    }
}

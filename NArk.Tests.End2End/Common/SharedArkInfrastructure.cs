namespace NArk.Tests.End2End.Core;

[SetUpFixture]
public class SharedArkInfrastructure
{
    public static readonly Uri ArkdEndpoint = new("http://localhost:7070");
    public static readonly Uri NbxplorerEndpoint = new("http://localhost:32838");
    // denigiri replaced nigiri's standalone chopsticks/esplora with mempool,
    // which serves the Esplora-compatible REST API under /api. The trailing
    // slash is required so HttpClient base-address resolution keeps the /api
    // prefix when EsploraBlockchain appends relative paths (e.g. "blocks/tip/hash").
    public static readonly Uri ChopsticksEndpoint = new("http://localhost:3000/api/");

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
                "  node regtest/regtest.mjs start --profile boltz,delegate\n\n" +
                $"Health check failed: {ex.Message}");
        }
    }
}

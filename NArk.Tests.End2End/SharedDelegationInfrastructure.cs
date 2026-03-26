using NArk.Tests.End2End.Core;

namespace NArk.Tests.End2End.Delegation;

[SetUpFixture]
public class SharedDelegationInfrastructure
{
    /// <summary>REST endpoint for wallet operations (port 7001 internal → 7011 external).</summary>
    public static readonly Uri DelegatorWalletEndpoint = new("http://localhost:7011");

    /// <summary>Delegator gRPC + REST endpoint (port 7002 internal → 7012 external).
    /// Fulmine v0.3.15 runs the delegator on a separate port from the main gRPC/REST server.</summary>
    public static readonly Uri DelegatorEndpoint = new("http://localhost:7012");

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        ThreadPool.SetMinThreads(50, 50);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Verify arkd and delegator are running
        foreach (var (name, url) in new[]
                 {
                     ("arkd", $"{SharedArkInfrastructure.ArkdEndpoint}/v1/info"),
                     ("delegator wallet", $"{DelegatorWalletEndpoint}/api/v1/wallet/status")
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
                    "  ./regtest/start-env.sh\n\n" +
                    $"Health check failed: {ex.Message}");
            }
        }
    }
}

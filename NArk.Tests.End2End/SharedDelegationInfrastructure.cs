using NArk.Tests.End2End.Core;

namespace NArk.Tests.End2End.Delegation;

[SetUpFixture]
public class SharedDelegationInfrastructure
{
    /// <summary>REST endpoint for health checks and wallet operations.</summary>
    public static readonly Uri DelegatorRestEndpoint = new("http://localhost:7011");

    /// <summary>gRPC endpoint for GrpcDelegatorProvider.</summary>
    public static readonly Uri DelegatorGrpcEndpoint = new("http://localhost:7010");

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        ThreadPool.SetMinThreads(50, 50);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Verify arkd is running (delegation tests also need it)
        foreach (var (name, url) in new[]
                 {
                     ("arkd", $"{SharedArkInfrastructure.ArkdEndpoint}/v1/info"),
                     ("delegator", $"{DelegatorRestEndpoint}/api/v1/wallet/status")
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
    }
}

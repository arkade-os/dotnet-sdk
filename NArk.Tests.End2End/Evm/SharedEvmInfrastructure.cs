using System.Text;

namespace NArk.Tests.End2End.Evm;

/// <summary>
/// EVM counterpart of <c>NArk.Tests.End2End.Core.SharedArkInfrastructure</c> — checks a local
/// Anvil node is reachable before any test in this namespace runs. Unlike the Bitcoin/Ark
/// regtest stack (nigiri/arkd/boltz), Anvil has nothing to do with that infrastructure and
/// isn't started by <c>regtest/regtest.mjs</c> — it's a single, disposable EVM node you start
/// yourself (<c>anvil</c>, from Foundry).
/// </summary>
[SetUpFixture]
public class SharedEvmInfrastructure
{
    public static readonly string AnvilRpcUrl = Environment.GetEnvironmentVariable("ANVIL_RPC_URL") ?? "http://localhost:8545";

    /// <summary>
    /// Anvil's default account #0 (from its well-known dev mnemonic "test test test test test
    /// test test test test test test junk") — pre-funded on every fresh Anvil instance, used
    /// here as the deployer/lock/claim/refund account for these tests.
    /// </summary>
    public const string DeployerPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var payload = new StringContent(
                """{"jsonrpc":"2.0","method":"eth_chainId","params":[],"id":1}""",
                Encoding.UTF8, "application/json");
            var response = await http.PostAsync(AnvilRpcUrl, payload);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Assert.Fail(
                "Anvil not running. Start it with:\n" +
                "  anvil\n\n" +
                $"(set ANVIL_RPC_URL to point elsewhere; defaults to {AnvilRpcUrl})\n\n" +
                $"Health check failed: {ex.Message}");
        }
    }
}

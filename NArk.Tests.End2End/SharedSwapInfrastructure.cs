using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using NArk.Tests.End2End.Common;

namespace NArk.Tests.End2End.Swaps;

[SetUpFixture]
public class SharedSwapInfrastructure
{
    public static DistributedApplication App { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        ThreadPool.SetMinThreads(50, 50);

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: [],
                configureBuilder: (appOptions, _) => { appOptions.AllowUnsecuredTransport = true; }
            );

        App = await builder.BuildAsync();
        await App.StartAsync(CancellationToken.None);
        var waitForBoltzHealthTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await App.ResourceNotifications.WaitForResourceHealthyAsync("boltz", waitForBoltzHealthTimeout.Token);

        var chopsticksEndpoint = App.GetEndpoint("chopsticks", "http");

        // Fund the Bitcoin Core default wallet so Boltz's minWalletBalance check passes.
        var addrResult = await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "getnewaddress"])
            .ExecuteBufferedAsync();
        var walletAddr = addrResult.StandardOutput.Trim();
        await new HttpClient().PostAsJsonAsync($"{chopsticksEndpoint}/faucet", new
        {
            amount = 1,
            address = walletAddr
        });

        // Send additional BTC to Fulmine's boarding address so it has enough ARK liquidity
        // for all swap tests (reverse swaps, chain swaps, etc.)
        var fulmineEndpoint = App.GetEndpoint("boltz-fulmine", "api");
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(fulmineEndpoint.ToString()) };
        var addressJson = await fulmineHttp.GetStringAsync("/api/v1/address");
        var arkAddress = JsonNode.Parse(addressJson)?["address"]?.GetValue<string>()
                         ?? throw new InvalidOperationException("Could not get Fulmine address");
        var onchainAddress = new Uri(arkAddress).AbsolutePath;
        Console.WriteLine($"[SwapInfra] Funding Fulmine boarding address: {onchainAddress}");

        await new HttpClient().PostAsJsonAsync($"{chopsticksEndpoint}/faucet", new
        {
            amount = 5,
            address = onchainAddress
        });

        // Mine blocks to confirm all funding txs and allow OnResourceReady callbacks
        // (including Fulmine settle) to complete via batch rounds.
        for (var i = 0; i < 10; i++)
            await App.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");

        // Ensure Fulmine has settled enough ARK VTXOs for all swap tests.
        await FulmineLiquidityHelper.EnsureArkLiquidity(App);
    }

    [OneTimeTearDown]
    public async Task GlobalTeardown()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

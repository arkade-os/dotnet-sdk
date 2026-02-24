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

        // Mine blocks to confirm any pending txs from OnResourceReady callbacks
        // and mature coinbase outputs for bitcoin-cli sendtoaddress.
        for (var i = 0; i < 6; i++)
            await App.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");

        // Ensure Fulmine has enough ARK VTXOs for all swap tests.
        // This funds the boarding address via bitcoin-cli, mines, settles, and repeats until balance is sufficient.
        await FulmineLiquidityHelper.EnsureArkLiquidity(App);
    }

    [OneTimeTearDown]
    public async Task GlobalTeardown()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

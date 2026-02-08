using Aspire.Hosting;

namespace NArk.Tests.End2End.Core;

[SetUpFixture]
public class SharedArkInfrastructure
{
    public static DistributedApplication App { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        ThreadPool.SetMinThreads(50, 50);

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: ["--noswap"],
                configureBuilder: (appOptions, _) => { appOptions.AllowUnsecuredTransport = true; }
            );

        App = await builder.BuildAsync();
        await App.StartAsync(CancellationToken.None);
        await App.ResourceNotifications.WaitForResourceHealthyAsync("ark", CancellationToken.None);
    }

    [OneTimeTearDown]
    public async Task GlobalTeardown()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

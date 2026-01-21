namespace NArk.Transport.GrpcClient.Tests;

public class TransportTests
{
    [Test]
    // Network is never reliable...
    [Retry(5)]
    public void CanConnectToMainnetArk()
    {
        var transport = new GrpcClientTransport("https://arkade.computer");
        Assert.DoesNotThrowAsync(async () => await transport.GetServerInfoAsync());
    }
}
using System.Net.Http.Json;
using NArk.Transport.GrpcClient;

namespace NArk.Tests.End2End.DelegatorServer;

public class DelegatorServerTests
{
    [Test]
    public async Task GetDelegatorInfo_via_grpc_and_rest_returns_our_pubkey_and_fee()
    {
        await using var host = await InProcessDelegatorHost.StartAsync(fee: "123");

        // gRPC against our in-process server.
        var provider = new GrpcDelegatorProvider(host.BaseUrl);
        var grpcInfo = await provider.GetDelegatorInfoAsync();
        Assert.That(grpcInfo.Pubkey, Is.Not.Null.And.Not.Empty);
        Assert.That(grpcInfo.Fee, Is.EqualTo("123"));

        // REST (JSON transcoding off the proto's google.api.http annotation).
        using var http = new HttpClient();
        var rest = await http.GetFromJsonAsync<InfoDto>($"{host.RestBaseUrl}/v1/delegator/info");
        Assert.That(rest!.Pubkey, Is.EqualTo(grpcInfo.Pubkey));
        Assert.That(rest.Fee, Is.EqualTo("123"));

        TestContext.Progress.WriteLine($"Our delegator pubkey: {grpcInfo.Pubkey}");
    }

    private record InfoDto(string Pubkey, string Fee, string DelegatorAddress);
}

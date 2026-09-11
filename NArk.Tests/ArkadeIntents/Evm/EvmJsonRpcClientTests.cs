using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Evm;

namespace NArk.Tests.ArkadeIntents.Evm;

public class EvmJsonRpcClientTests
{
    [Test]
    public async Task ReadsChainBlockCallAndHistoricalTimestamp()
    {
        var handler = new RpcHandler(request => request["method"]!.GetValue<string>() switch
        {
            "eth_chainId" => Result(request, "0x7a69"),
            "eth_blockNumber" => Result(request, "0x64"),
            "eth_getBlockByNumber" => Result(request, new JsonObject { ["timestamp"] = "0x3e8" }),
            "eth_call" => Result(request, "0x" + new string('0', 63) + "1"),
            _ => throw new AssertionException("unexpected RPC method"),
        });
        var rpc = Client(handler);

        Assert.Multiple(async () =>
        {
            Assert.That(await rpc.GetChainIdAsync(), Is.EqualTo(new BigInteger(31_337)));
            Assert.That(await rpc.GetBlockNumberAsync(), Is.EqualTo(new BigInteger(100)));
            Assert.That(await rpc.GetBlockTimestampAsync(99), Is.EqualTo(1_000));
            Assert.That(await rpc.CallAsync("0x1111111111111111111111111111111111111111", [0xaa], 99),
                Is.EqualTo(Convert.FromHexString(new string('0', 63) + "1")));
        });
        var historical = handler.Requests.Single(r => r["method"]!.GetValue<string>() == "eth_call");
        Assert.That(historical["params"]![1]!.GetValue<string>(), Is.EqualTo("0x63"));
    }

    [Test]
    public void RpcErrorPreservesCodeButNeverNodeTextThatCouldContainAPreimage()
    {
        var secret = new string('a', 64);
        var handler = new RpcHandler(request => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
            ["error"] = new JsonObject
                { ["code"] = -32_000, ["message"] = "execution reverted calldata=0xbc586b28" + secret },
        });

        var error = Assert.ThrowsAsync<EvmJsonRpcException>(async () => await Client(handler).GetChainIdAsync());

        Assert.Multiple(() =>
        {
            Assert.That(error!.Code, Is.EqualTo(-32_000));
            Assert.That(error.ToString(), Does.Contain("eth_chainId"));
            Assert.That(error.ToString(), Does.Not.Contain(secret));
            Assert.That(error.ToString(), Does.Not.Contain("execution reverted"));
        });
    }

    [TestCase(false, TestName = "Content_length_oversize_is_refused")]
    [TestCase(true, TestName = "Chunked_oversize_is_refused")]
    public void OversizedResponsesAreRefusedWithoutIncludingTheirBody(bool chunked)
    {
        var secret = new string('d', 2_000);
        HttpContent content = chunked
            ? new ChunkedContent(Encoding.UTF8.GetBytes(secret)) : new StringContent(secret);
        var rpc = new EvmJsonRpcClient(
            new HttpClient(new StaticResponseHandler(content)) { BaseAddress = new Uri("http://evm.test/") },
            options: new EvmJsonRpcOptions { MaxResponseBytes = 512 });

        var error = Assert.ThrowsAsync<EvmJsonRpcException>(async () => await rpc.GetChainIdAsync());

        Assert.That(error!.ToString(), Does.Not.Contain(secret));
    }

    [Test]
    public void ResponseIsNotEagerlyBufferedBeforeTheByteLimit()
    {
        var content = new HeadersReadOnlyContent(Encoding.UTF8.GetBytes(new string('e', 2_000)));
        var rpc = new EvmJsonRpcClient(
            new HttpClient(new StaticResponseHandler(content)) { BaseAddress = new Uri("http://evm.test/") },
            options: new EvmJsonRpcOptions { MaxResponseBytes = 512 });

        Assert.That(async () => await rpc.GetChainIdAsync(), Throws.TypeOf<EvmJsonRpcException>());
        Assert.That(content.SerializeCalls, Is.Zero);
    }

    [Test]
    public void ExcessivelyDeepJsonIsRefused()
    {
        var nested = new JsonObject();
        var cursor = nested;
        for (var i = 0; i < 12; i++)
        {
            var child = new JsonObject();
            cursor["x"] = child;
            cursor = child;
        }
        var handler = new RpcHandler(request => Result(request, nested));
        var rpc = Client(handler, maxJsonDepth: 8);

        Assert.That(async () => await rpc.GetChainIdAsync(), Throws.TypeOf<EvmJsonRpcException>());
    }

    [Test]
    public void MalformedErrorEnvelopeIsReportedWithoutEchoingNodeFields()
    {
        const string secret = "claimFor-preimage-must-not-escape";
        var handler = new RpcHandler(request => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
            ["error"] = new JsonObject { ["code"] = secret, ["message"] = secret },
        });

        var error = Assert.ThrowsAsync<EvmJsonRpcException>(async () => await Client(handler).GetChainIdAsync());

        Assert.That(error!.ToString(), Does.Not.Contain(secret));
    }

    [Test]
    public async Task ReceiptPollingWaitsForMinedReceiptAndParsesLogs()
    {
        var attempts = 0;
        var hash = "0x" + new string('a', 64);
        var handler = new RpcHandler(request => ++attempts == 1 ? Result(request, null) : Result(request,
            new JsonObject
            {
                ["transactionHash"] = hash, ["status"] = "0x1",
                ["logs"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = "0x1111111111111111111111111111111111111111",
                        ["topics"] = new JsonArray("0x" + new string('b', 64)), ["data"] = "0x1234",
                    },
                },
            }));

        var receipt = await Client(handler, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1))
            .WaitForReceiptAsync(hash);

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(receipt.Succeeded, Is.True);
            Assert.That(receipt.Logs.Single().Topics.Single(), Is.EqualTo("0x" + new string('b', 64)));
        });
    }

    [Test]
    public async Task ReceiptStatusZeroIsReturnedAsARevert()
    {
        var hash = "0x" + new string('a', 64);
        var handler = new RpcHandler(request => Result(request, new JsonObject
        {
            ["transactionHash"] = hash, ["status"] = "0x0", ["logs"] = new JsonArray(),
        }));

        var receipt = await Client(handler).WaitForReceiptAsync(hash);

        Assert.That(receipt.Succeeded, Is.False);
    }

    [Test]
    public void ReceiptPollingHonorsTimeoutAndCallerCancellation()
    {
        var hash = "0x" + new string('a', 64);
        var handler = new RpcHandler(request => Result(request, null));
        var rpc = Client(handler, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10));

        Assert.That(async () => await rpc.WaitForReceiptAsync(hash), Throws.TypeOf<TimeoutException>());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.That(async () => await rpc.WaitForReceiptAsync(hash, cancelled.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    internal static EvmJsonRpcClient Client(
        RpcHandler handler,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        int maxJsonDepth = 32) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://evm.test/") },
        options: new EvmJsonRpcOptions
        {
            ReceiptPollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10),
            ReceiptTimeout = timeout ?? TimeSpan.FromSeconds(1),
            MaxJsonDepth = maxJsonDepth,
        });

    internal static JsonObject Result(JsonObject request, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result,
    };

    internal sealed class RpcHandler(
        Func<JsonObject, JsonObject> respond,
        TimeSpan? responseDelay = null) : HttpMessageHandler
    {
        private readonly object _sync = new();
        public List<JsonObject> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (responseDelay is not null)
                await Task.Delay(responseDelay.Value, cancellationToken);
            JsonObject response;
            lock (_sync)
            {
                Requests.Add((JsonObject)body.DeepClone());
                response = respond(body);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StaticResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class HeadersReadOnlyContent(byte[] bytes) : HttpContent
    {
        public int SerializeCalls { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializeCalls++;
            throw new AssertionException("response body was eagerly buffered");
        }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

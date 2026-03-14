using System.Net;
using Microsoft.Extensions.Options;
using NArk.Swaps.Abstractions;
using NArk.Swaps.LendaSwap;
using NArk.Swaps.LendaSwap.Client;
using NArk.Swaps.LendaSwap.Models;
using NArk.Swaps.Models;

namespace NArk.Tests;

[TestFixture]
public class LendaSwapClientTests
{
    private const string BaseUrl = "https://api.lendaswap.com";

    // ─── MockHttpHandler ───────────────────────────────────────

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;
        public HttpRequestMessage? LastRequest { get; private set; }

        public MockHttpHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static LendaSwapClient CreateClient(
        MockHttpHandler handler, string? apiKey = null)
    {
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new LendaSwapOptions
        {
            ApiUrl = BaseUrl,
            ApiKey = apiKey
        });
        return new LendaSwapClient(httpClient, options);
    }

    // ─── GetTokensAsync ────────────────────────────────────────

    [Test]
    public async Task GetTokensAsync_DeserializesResponse()
    {
        const string json = """
        {
            "btc_tokens": [
                {"token_id": "btc", "symbol": "BTC", "name": "Bitcoin", "decimals": 8, "chain": "Bitcoin"}
            ],
            "evm_tokens": [
                {"token_id": "0x3c499c", "symbol": "USDC", "name": "USD Coin", "decimals": 6, "chain": "137"}
            ]
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var result = await client.GetTokensAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BtcTokens, Has.Count.EqualTo(1));
        Assert.That(result.BtcTokens[0].TokenId, Is.EqualTo("btc"));
        Assert.That(result.BtcTokens[0].Symbol, Is.EqualTo("BTC"));
        Assert.That(result.BtcTokens[0].Decimals, Is.EqualTo(8));
        Assert.That(result.BtcTokens[0].Chain, Is.EqualTo("Bitcoin"));
        Assert.That(result.EvmTokens, Has.Count.EqualTo(1));
        Assert.That(result.EvmTokens[0].TokenId, Is.EqualTo("0x3c499c"));
        Assert.That(result.EvmTokens[0].Symbol, Is.EqualTo("USDC"));
        Assert.That(result.EvmTokens[0].Chain, Is.EqualTo("137"));

        Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery, Is.EqualTo("/tokens"));
    }

    // ─── GetQuoteAsync ─────────────────────────────────────────

    [Test]
    public async Task GetQuoteAsync_SendsCorrectQueryParams()
    {
        const string json = """
        {
            "exchange_rate": "96500.00",
            "protocol_fee": 250,
            "protocol_fee_rate": 0.0025,
            "network_fee": 150,
            "gasless_network_fee": 50,
            "source_amount": 100000,
            "target_amount": 96100,
            "min_amount": 10000,
            "max_amount": 10000000
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var result = await client.GetQuoteAsync(
            "Bitcoin", "btc", "Arkade", "btc", sourceAmount: 100000);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ExchangeRate, Is.EqualTo("96500.00"));
        Assert.That(result.ProtocolFee, Is.EqualTo(250));
        Assert.That(result.ProtocolFeeRate, Is.EqualTo(0.0025m));
        Assert.That(result.NetworkFee, Is.EqualTo(150));
        Assert.That(result.SourceAmount, Is.EqualTo(100000));
        Assert.That(result.TargetAmount, Is.EqualTo(96100));
        Assert.That(result.MinAmount, Is.EqualTo(10000));
        Assert.That(result.MaxAmount, Is.EqualTo(10000000));

        var uri = handler.LastRequest!.RequestUri!;
        Assert.That(uri.PathAndQuery, Does.Contain("source_chain=Bitcoin"));
        Assert.That(uri.PathAndQuery, Does.Contain("source_token=btc"));
        Assert.That(uri.PathAndQuery, Does.Contain("target_chain=Arkade"));
        Assert.That(uri.PathAndQuery, Does.Contain("target_token=btc"));
        Assert.That(uri.PathAndQuery, Does.Contain("source_amount=100000"));
    }

    [Test]
    public async Task GetQuoteAsync_OmitsNullAmountParams()
    {
        const string json = """
        {
            "exchange_rate": "1.00",
            "protocol_fee": 0,
            "protocol_fee_rate": 0,
            "network_fee": 0,
            "gasless_network_fee": 0,
            "source_amount": 0,
            "target_amount": 0,
            "min_amount": 0,
            "max_amount": 0
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        await client.GetQuoteAsync("Arkade", "btc", "137", "0x3c499c");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.That(query, Does.Not.Contain("source_amount"));
        Assert.That(query, Does.Not.Contain("target_amount"));
    }

    // ─── CreateBtcToArkadeSwapAsync ────────────────────────────

    [Test]
    public async Task CreateBtcToArkadeSwapAsync_DeserializesResponse()
    {
        const string json = """
        {
            "id": "swap-123",
            "status": "pending",
            "btc_htlc_address": "bc1qexample",
            "arkade_vhtlc_address": "tark1qexample",
            "source_amount": 100000,
            "target_amount": 96100,
            "protocol_fee": 250,
            "network_fee": 150,
            "created_at": "2026-02-27T12:00:00Z",
            "expires_at": "2026-02-27T13:00:00Z",
            "btc_locktime": 144,
            "arkade_locktime": 72,
            "hash_lock": "abc123",
            "server_pk": "02def456"
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var request = new CreateBtcToArkadeRequest
        {
            ClaimPk = "02aabbcc",
            HashLock = "abc123",
            RefundPk = "02ddeeff",
            SatsReceive = 96100,
            TargetArkadeAddress = "tark1qexample",
            UserId = "user-1"
        };

        var result = await client.CreateBtcToArkadeSwapAsync(request);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo("swap-123"));
        Assert.That(result.Status, Is.EqualTo("pending"));
        Assert.That(result.BtcHtlcAddress, Is.EqualTo("bc1qexample"));
        Assert.That(result.ArkadeVhtlcAddress, Is.EqualTo("tark1qexample"));
        Assert.That(result.SourceAmount, Is.EqualTo(100000));
        Assert.That(result.TargetAmount, Is.EqualTo(96100));
        Assert.That(result.ProtocolFee, Is.EqualTo(250));
        Assert.That(result.NetworkFee, Is.EqualTo(150));
        Assert.That(result.BtcLocktime, Is.EqualTo(144));
        Assert.That(result.ArkadeLocktime, Is.EqualTo(72));
        Assert.That(result.HashLock, Is.EqualTo("abc123"));
        Assert.That(result.ServerPk, Is.EqualTo("02def456"));

        Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery, Is.EqualTo("/swap/bitcoin/arkade"));
        Assert.That(handler.LastRequest.Method, Is.EqualTo(HttpMethod.Post));
    }

    // ─── GetSwapStatusAsync ────────────────────────────────────

    [Test]
    public async Task GetSwapStatusAsync_DeserializesResponse()
    {
        const string json = """
        {
            "id": "swap-456",
            "status": "clientredeemed",
            "source_amount": 50000,
            "target_amount": 48000,
            "protocol_fee": 125,
            "network_fee": 75
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var result = await client.GetSwapStatusAsync("swap-456");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo("swap-456"));
        Assert.That(result.Status, Is.EqualTo("clientredeemed"));

        Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery, Is.EqualTo("/swap/swap-456"));
        Assert.That(handler.LastRequest.Method, Is.EqualTo(HttpMethod.Get));
    }

    // ─── API Key Header ────────────────────────────────────────

    [Test]
    public async Task ApiKeyHeader_SentWhenConfigured()
    {
        const string json = """{ "btc_tokens": [], "evm_tokens": [] }""";
        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler, apiKey: "test-api-key-123");

        await client.GetTokensAsync();

        Assert.That(handler.LastRequest!.Headers.Contains("X-API-Key"), Is.True);
        Assert.That(
            handler.LastRequest.Headers.GetValues("X-API-Key").First(),
            Is.EqualTo("test-api-key-123"));
    }

    [Test]
    public async Task ApiKeyHeader_NotSentWhenNotConfigured()
    {
        const string json = """{ "btc_tokens": [], "evm_tokens": [] }""";
        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler, apiKey: null);

        await client.GetTokensAsync();

        Assert.That(handler.LastRequest!.Headers.Contains("X-API-Key"), Is.False);
    }

    // ─── HTTP Error Handling ───────────────────────────────────

    [Test]
    public void CreateBtcToArkadeSwapAsync_ThrowsOnHttpError()
    {
        const string errorJson = """{ "error": "invalid_request", "message": "hash_lock is required" }""";
        var handler = new MockHttpHandler(HttpStatusCode.BadRequest, errorJson);
        var client = CreateClient(handler);

        var request = new CreateBtcToArkadeRequest
        {
            ClaimPk = "02aabbcc",
            HashLock = "",
            RefundPk = "02ddeeff",
            SatsReceive = 100,
            TargetArkadeAddress = "tark1q",
            UserId = "user-1"
        };

        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateBtcToArkadeSwapAsync(request));
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ─── Status Mapping ────────────────────────────────────────

    [TestCase("pending", ArkSwapStatus.Pending)]
    [TestCase("clientfundingseen", ArkSwapStatus.Pending)]
    [TestCase("clientfunded", ArkSwapStatus.Pending)]
    [TestCase("serverfunded", ArkSwapStatus.Pending)]
    [TestCase("clientredeeming", ArkSwapStatus.Pending)]
    [TestCase("clientredeemed", ArkSwapStatus.Settled)]
    [TestCase("serverredeemed", ArkSwapStatus.Settled)]
    [TestCase("clientrefunded", ArkSwapStatus.Refunded)]
    [TestCase("clientrefundedserverfunded", ArkSwapStatus.Refunded)]
    [TestCase("clientrefundedserverrefunded", ArkSwapStatus.Refunded)]
    [TestCase("expired", ArkSwapStatus.Failed)]
    [TestCase("clientinvalidfunded", ArkSwapStatus.Failed)]
    [TestCase("clientfundedtoolate", ArkSwapStatus.Failed)]
    [TestCase("clientfundedserverrefunded", ArkSwapStatus.Failed)]
    [TestCase("clientredeemedandclientrefunded", ArkSwapStatus.Failed)]
    [TestCase("some_unknown_status", ArkSwapStatus.Unknown)]
    public void MapLendaSwapStatus_MapsCorrectly(string apiStatus, ArkSwapStatus expected)
    {
        var result = LendaSwapProvider.MapLendaSwapStatus(apiStatus);
        Assert.That(result, Is.EqualTo(expected));
    }

    // ─── Chain / Network Mapping ───────────────────────────────

    [TestCase("Arkade", SwapNetwork.Ark)]
    [TestCase("Bitcoin", SwapNetwork.BitcoinOnchain)]
    [TestCase("Lightning", SwapNetwork.Lightning)]
    [TestCase("1", SwapNetwork.EvmEthereum)]
    [TestCase("137", SwapNetwork.EvmPolygon)]
    [TestCase("42161", SwapNetwork.EvmArbitrum)]
    public void MapChainToNetwork_MapsKnownChains(string chain, SwapNetwork expected)
    {
        var result = LendaSwapProvider.MapChainToNetwork(chain);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void MapChainToNetwork_ReturnsNullForUnknown()
    {
        var result = LendaSwapProvider.MapChainToNetwork("SomeUnknownChain");
        Assert.That(result, Is.Null);
    }

    // ─── Route Mapping ─────────────────────────────────────────

    [Test]
    public void MapRouteToApiParams_BtcToArk()
    {
        var route = new SwapRoute(
            SwapAsset.BtcOnchain,
            SwapAsset.ArkBtc);

        var (sourceChain, sourceToken, targetChain, targetToken) =
            LendaSwapProvider.MapRouteToApiParams(route);

        Assert.That(sourceChain, Is.EqualTo("Bitcoin"));
        Assert.That(sourceToken, Is.EqualTo("btc"));
        Assert.That(targetChain, Is.EqualTo("Arkade"));
        Assert.That(targetToken, Is.EqualTo("btc"));
    }

    [Test]
    public void MapRouteToApiParams_ArkToEvmPolygon()
    {
        var route = new SwapRoute(
            SwapAsset.ArkBtc,
            SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0x3c499c"));

        var (sourceChain, sourceToken, targetChain, targetToken) =
            LendaSwapProvider.MapRouteToApiParams(route);

        Assert.That(sourceChain, Is.EqualTo("Arkade"));
        Assert.That(sourceToken, Is.EqualTo("btc"));
        Assert.That(targetChain, Is.EqualTo("137"));
        Assert.That(targetToken, Is.EqualTo("0x3c499c"));
    }

    // ─── Route Support ─────────────────────────────────────────

    [Test]
    public void SupportsRoute_BtcToArk_True()
    {
        var provider = CreateProviderForRouteTests();
        var route = new SwapRoute(
            SwapAsset.BtcOnchain,
            SwapAsset.ArkBtc);
        Assert.That(provider.SupportsRoute(route), Is.True);
    }

    [Test]
    public void SupportsRoute_ArkToEvmPolygon_True()
    {
        var provider = CreateProviderForRouteTests();
        var route = new SwapRoute(
            SwapAsset.ArkBtc,
            SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0xusdc"));
        Assert.That(provider.SupportsRoute(route), Is.True);
    }

    [Test]
    public void SupportsRoute_EvmArbitrumToArk_True()
    {
        var provider = CreateProviderForRouteTests();
        var route = new SwapRoute(
            SwapAsset.Erc20(SwapNetwork.EvmArbitrum, "0xusdc"),
            SwapAsset.ArkBtc);
        Assert.That(provider.SupportsRoute(route), Is.True);
    }

    [Test]
    public void SupportsRoute_LightningToArk_False()
    {
        var provider = CreateProviderForRouteTests();
        var route = new SwapRoute(
            SwapAsset.BtcLightning,
            SwapAsset.ArkBtc);
        Assert.That(provider.SupportsRoute(route), Is.False);
    }

    [Test]
    public void SupportsRoute_ArkToLightning_False()
    {
        var provider = CreateProviderForRouteTests();
        var route = new SwapRoute(
            SwapAsset.ArkBtc,
            SwapAsset.BtcLightning);
        Assert.That(provider.SupportsRoute(route), Is.False);
    }

    // ─── CreateArkadeToEvmSwapAsync ─────────────────────────────

    [Test]
    public async Task CreateArkadeToEvmSwapAsync_DeserializesResponse()
    {
        const string json = """
        {
            "id": "swap-789",
            "status": "pending",
            "evm_htlc_address": "0xhtlccontract",
            "arkade_vhtlc_address": "tark1qswap",
            "source_amount": 200000,
            "target_amount": 19300,
            "protocol_fee": 500,
            "network_fee": 200,
            "evm_chain_id": "137",
            "token_address": "0x3c499c"
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var request = new CreateArkadeToEvmRequest
        {
            HashLock = "hash123",
            RefundPk = "02refund",
            ClaimingAddress = "tark1qclaim",
            TargetAddress = "0xevmaddress",
            TokenAddress = "0x3c499c",
            EvmChainId = "137",
            UserId = "user-2",
            AmountIn = 200000
        };

        var result = await client.CreateArkadeToEvmSwapAsync(request);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo("swap-789"));
        Assert.That(result.Status, Is.EqualTo("pending"));
        Assert.That(result.EvmHtlcAddress, Is.EqualTo("0xhtlccontract"));
        Assert.That(result.EvmChainId, Is.EqualTo("137"));
        Assert.That(result.TokenAddress, Is.EqualTo("0x3c499c"));

        Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery, Is.EqualTo("/swap/arkade/evm"));
        Assert.That(handler.LastRequest.Method, Is.EqualTo(HttpMethod.Post));
    }

    // ─── CreateEvmToArkadeSwapAsync ─────────────────────────────

    [Test]
    public async Task CreateEvmToArkadeSwapAsync_DeserializesResponse()
    {
        const string json = """
        {
            "id": "swap-evm2ark",
            "status": "pending",
            "evm_htlc_address": "0xhtlc2",
            "arkade_vhtlc_address": "tark1qreceive",
            "source_amount": 20000,
            "target_amount": 190000,
            "protocol_fee": 300,
            "network_fee": 100
        }
        """;

        var handler = new MockHttpHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var request = new CreateEvmToArkadeRequest
        {
            HashLock = "hash456",
            ReceiverPk = "02receiver",
            TargetAddress = "tark1qreceive",
            TokenAddress = "0xusdc",
            EvmChainId = "1",
            UserAddress = "0xuser",
            UserId = "user-3",
            AmountIn = 20000
        };

        var result = await client.CreateEvmToArkadeSwapAsync(request);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo("swap-evm2ark"));
        Assert.That(result.Status, Is.EqualTo("pending"));

        Assert.That(handler.LastRequest!.RequestUri!.PathAndQuery, Is.EqualTo("/swap/evm/arkade"));
        Assert.That(handler.LastRequest.Method, Is.EqualTo(HttpMethod.Post));
    }

    // ─── Helper ────────────────────────────────────────────────

    private static LendaSwapProvider CreateProviderForRouteTests()
    {
        // Create a minimal provider just for route testing — swap storage is mocked with NSubstitute
        var handler = new MockHttpHandler(HttpStatusCode.OK, "{}");
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new LendaSwapOptions { ApiUrl = BaseUrl });
        var client = new LendaSwapClient(httpClient, options);
        var swapStorage = NSubstitute.Substitute.For<ISwapStorage>();
        return new LendaSwapProvider(client, swapStorage);
    }
}

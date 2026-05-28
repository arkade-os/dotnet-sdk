using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Common;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.End2End.Mocks;

/// <summary>
/// Controls how the mock responds to cooperative claim requests from the SDK.
/// </summary>
public enum ClaimMode { Normal, Fail }

/// <summary>
/// Controls how the mock responds to cooperative refund requests from the SDK.
/// </summary>
public enum RefundMode { Normal, Fail }

/// <summary>
/// Runtime configuration for <see cref="MockBoltzServer"/>.
/// Mutate this via <see cref="MockBoltzServer.Config"/> between test steps.
/// </summary>
public sealed class MockBoltzConfig
{
    public ClaimMode ClaimMode { get; set; } = ClaimMode.Normal;
    public RefundMode RefundMode { get; set; } = RefundMode.Normal;
}

/// <summary>
/// In-process HTTP + WebSocket mock of the Boltz swap API, suitable for E2E tests
/// that need deterministic control over swap status transitions (e.g. "swap.expired",
/// "invoice.failedToPay") without depending on real LND or mempool timing.
///
/// <para>Usage:</para>
/// <code>
/// await using var mockBoltz = await MockBoltzServer.StartAsync();
/// // wire BoltzClient to mockBoltz.BaseUrl / WsBaseUrl
/// var swapId = await sut.InitiateArkToBtcChainSwap(...);
/// await mockBoltz.PushSwapEvent(swapId, "swap.expired");
/// await WaitForStatus(swapId, ArkSwapStatus.Refunded, TimeSpan.FromSeconds(30));
/// Assert.That(mockBoltz.ChainArkRefundRequestsFor(swapId), Is.GreaterThan(0));
/// </code>
///
/// <para>
/// For swap-creation endpoints that validate the computed ARK address
/// (submarine, reverse, BTC→ARK chain), set <see cref="ServerInfo"/> to the
/// <see cref="ArkServerInfo"/> of the running arkd instance so the mock can
/// compute the correct VHTLC address.  Without it the mock returns a
/// syntactically-valid but semantically-random ARK address, which will fail
/// the SDK's address-mismatch check.
/// </para>
/// </summary>
public sealed class MockBoltzServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, MockSwapState> _swaps = new();
    private readonly List<WsSession> _sessions = new();
    private readonly SemaphoreSlim _sessionsLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>HTTP base URL of the mock, e.g. <c>http://127.0.0.1:54321</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>WebSocket base URL of the mock, e.g. <c>ws://127.0.0.1:54321</c>.</summary>
    public string WsBaseUrl { get; }

    /// <summary>
    /// Mutable runtime config. Change <see cref="MockBoltzConfig.ClaimMode"/> /
    /// <see cref="MockBoltzConfig.RefundMode"/> between test steps to simulate failure modes.
    /// </summary>
    public MockBoltzConfig Config { get; } = new();

    /// <summary>
    /// When set, the mock uses this server info to compute real VHTLC addresses for
    /// submarine, reverse, and BTC→ARK chain swap creation responses.
    /// Without it, a syntactically-valid but random ARK address is returned, which will
    /// fail the SDK's address-mismatch check in <c>BoltzSwapsService</c>.
    /// </summary>
    public ArkServerInfo? ServerInfo { get; set; }

    private MockBoltzServer(WebApplication app, int port)
    {
        _app = app;
        BaseUrl = $"http://127.0.0.1:{port}";
        WsBaseUrl = $"ws://127.0.0.1:{port}";
    }

    /// <summary>
    /// Starts the mock on a random free port and returns when ready to accept connections.
    /// Dispose the returned instance (or <c>await using</c>) to stop it.
    /// </summary>
    public static async Task<MockBoltzServer> StartAsync()
    {
        var port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        var server = new MockBoltzServer(app, port);
        server.RegisterRoutes(app);
        await app.StartAsync();
        return server;
    }

    // ── Route registration ───────────────────────────────────────────

    private void RegisterRoutes(WebApplication app)
    {
        app.MapGet("/version", () => Results.Ok(new { version = "mock-boltz" }));

        app.MapGet("/v2/swap/submarine", () => Results.Json(BuildSubmarinePairs(), JsonOpts));
        app.MapGet("/v2/swap/reverse", () => Results.Json(BuildReversePairs(), JsonOpts));
        app.MapGet("/v2/swap/chain", () => Results.Json(BuildChainPairs(), JsonOpts));

        app.MapGet("/v2/swap/{swapId}", (string swapId) =>
        {
            if (_swaps.TryGetValue(swapId, out var s))
                return Results.Json(new SwapStatusResponse
                {
                    Status = s.Status,
                    Transaction = s.LockupTxHex is not null
                        ? new SwapStatusTransaction { Hex = s.LockupTxHex }
                        : null
                }, JsonOpts);
            return Results.NotFound(new { error = $"could not find swap with id {swapId}" });
        });

        app.MapPost("/v2/swap/submarine", (HttpRequest req) => HandleCreateSubmarineAsync(req));

        app.MapPost("/v2/swap/submarine/{swapId}/refund/ark", async (string swapId, HttpRequest req) =>
        {
            if (_swaps.TryGetValue(swapId, out var s))
                Interlocked.Increment(ref s.SubmarineRefundCount);
            if (Config.RefundMode == RefundMode.Fail)
                return Results.BadRequest(new { error = "refund refused (mock RefundMode.Fail)" });
            var body = await req.ReadFromJsonAsync<SubmarineRefundRequest>(JsonOpts);
            return Results.Json(new SubmarineRefundResponse
            {
                Transaction = body?.Transaction ?? "",
                Checkpoint = body?.Checkpoint ?? ""
            }, JsonOpts);
        });

        app.MapPost("/v2/swap/reverse", (HttpRequest req) => HandleCreateReverseAsync(req));

        app.MapPost("/v2/swap/chain", (HttpRequest req) => HandleCreateChainAsync(req));

        app.MapGet("/v2/swap/chain/{swapId}/claim", (string swapId) =>
        {
            if (!_swaps.ContainsKey(swapId))
                return Results.NotFound(new { error = $"could not find swap with id {swapId}" });
            return Results.Json(new ChainClaimDetails
            {
                PubNonce = RandHex(66),
                PublicKey = Hex(new Key().PubKey.ToBytes()),
                TransactionHash = RandHex(32)
            }, JsonOpts);
        });

        app.MapPost("/v2/swap/chain/{swapId}/claim", (string swapId) =>
        {
            if (_swaps.TryGetValue(swapId, out var s))
                Interlocked.Increment(ref s.ClaimCount);
            if (Config.ClaimMode == ClaimMode.Fail)
                return Results.BadRequest(new { error = "claim refused (mock ClaimMode.Fail)" });
            return Results.Json(new PartialSignatureData
            {
                PubNonce = RandHex(66),
                PartialSignature = RandHex(32)
            }, JsonOpts);
        });

        app.MapGet("/v2/swap/chain/{swapId}/quote", (string swapId) =>
        {
            if (!_swaps.TryGetValue(swapId, out var s))
                return Results.NotFound(new { error = $"could not find swap with id {swapId}" });
            return Results.Json(new ChainQuote { Amount = s.ExpectedAmount }, JsonOpts);
        });

        app.MapPost("/v2/swap/chain/{swapId}/quote", async (string swapId, HttpRequest req) =>
        {
            var quote = await req.ReadFromJsonAsync<ChainQuote>(JsonOpts);
            if (_swaps.TryGetValue(swapId, out var s) && quote is not null)
                s.ExpectedAmount = quote.Amount;
            return Results.Ok();
        });

        app.MapPost("/v2/swap/chain/{swapId}/refund", (string swapId) =>
        {
            if (_swaps.TryGetValue(swapId, out var s))
                Interlocked.Increment(ref s.ChainBtcRefundCount);
            if (Config.RefundMode == RefundMode.Fail)
                return Results.BadRequest(new { error = "refund refused (mock RefundMode.Fail)" });
            return Results.Json(new PartialSignatureData
            {
                PubNonce = RandHex(66),
                PartialSignature = RandHex(32)
            }, JsonOpts);
        });

        app.MapPost("/v2/swap/chain/{swapId}/refund/ark", async (string swapId, HttpRequest req) =>
        {
            if (_swaps.TryGetValue(swapId, out var s))
                Interlocked.Increment(ref s.ChainArkRefundCount);
            if (Config.RefundMode == RefundMode.Fail)
                return Results.BadRequest(new { error = "refund refused (mock RefundMode.Fail)" });
            var body = await req.ReadFromJsonAsync<ChainArkRefundRequest>(JsonOpts);
            return Results.Json(new ChainArkRefundResponse
            {
                Transaction = body?.Transaction ?? "",
                Checkpoint = body?.Checkpoint ?? ""
            }, JsonOpts);
        });

        app.MapPost("/v2/chain/BTC/transaction",
            () => Results.Json(new BroadcastResponse { Id = RandHex(32) }, JsonOpts));

        app.MapPost("/v2/swap/restore",
            () => Results.Ok(Array.Empty<object>()));

        app.Map("/v2/ws", HandleWebSocketAsync);
    }

    // ── Swap creation handlers ───────────────────────────────────────

    private async Task<IResult> HandleCreateSubmarineAsync(HttpRequest httpReq)
    {
        var req = await httpReq.ReadFromJsonAsync<SubmarineRequest>(JsonOpts);
        if (req is null) return Results.BadRequest();

        var swapId = NewSwapId();
        var boltzKey = new Key();
        var timeouts = DefaultTimeouts();

        string address;
        if (ServerInfo is not null)
        {
            try { address = ComputeSubmarineVhtlcAddress(req, boltzKey, ServerInfo, timeouts); }
            catch { address = FallbackArkAddress(); }
        }
        else
        {
            address = FallbackArkAddress();
        }

        _swaps[swapId] = new MockSwapState { Id = swapId, Status = "swap.created", ExpectedAmount = 50_000 };

        return Results.Json(new SubmarineResponse
        {
            Id = swapId,
            Address = address,
            ExpectedAmount = 50_000,
            ClaimPublicKey = Hex(boltzKey.PubKey.ToBytes()),
            AcceptZeroConf = true,
            TimeoutBlockHeights = timeouts
        }, JsonOpts);
    }

    private async Task<IResult> HandleCreateReverseAsync(HttpRequest httpReq)
    {
        var req = await httpReq.ReadFromJsonAsync<ReverseRequest>(JsonOpts);
        if (req is null) return Results.BadRequest();

        var swapId = NewSwapId();
        var boltzKey = new Key();
        var timeouts = DefaultTimeouts();
        var amount = req.OnchainAmount ?? req.InvoiceAmount ?? 50_000;

        string lockupAddress;
        if (ServerInfo is not null && req.ClaimPublicKey is not null && req.PreimageHash is not null)
        {
            try { lockupAddress = ComputeReverseVhtlcAddress(req, boltzKey, ServerInfo, timeouts); }
            catch { lockupAddress = FallbackArkAddress(); }
        }
        else
        {
            lockupAddress = FallbackArkAddress();
        }

        _swaps[swapId] = new MockSwapState { Id = swapId, Status = "swap.created", ExpectedAmount = amount };

        // The mock does not generate a real BOLT11 invoice — callers that need the full
        // reverse-swap validation (invoice payment-hash check) must configure ServerInfo
        // and provide a real invoice via a separate path, or use real Boltz for creation.
        var dummyInvoice = $"lnbcrt{amount}n1mockbolt11{swapId}";

        return Results.Json(new ReverseResponse
        {
            Id = swapId,
            LockupAddress = lockupAddress,
            RefundPublicKey = Hex(boltzKey.PubKey.ToBytes()),
            TimeoutBlockHeights = timeouts,
            Invoice = dummyInvoice
        }, JsonOpts);
    }

    private async Task<IResult> HandleCreateChainAsync(HttpRequest httpReq)
    {
        var req = await httpReq.ReadFromJsonAsync<ChainRequest>(JsonOpts);
        if (req is null) return Results.BadRequest();

        var swapId = NewSwapId();
        var boltzKey = new Key();
        var boltzPubHex = Hex(boltzKey.PubKey.ToBytes());
        var timeouts = DefaultTimeouts();

        ChainResponse resp;

        if (req.From == "ARK")
        {
            // ARK→BTC: no ARK-side address validation — any valid ARK address works.
            var amount = req.UserLockAmount > 0 ? req.UserLockAmount : 50_000;
            resp = new ChainResponse
            {
                Id = swapId,
                LockupDetails = new ChainSwapData
                {
                    LockupAddress = FallbackArkAddress(),
                    ServerPublicKey = boltzPubHex,
                    TimeoutBlockHeight = timeouts.Refund,
                    Timeouts = timeouts,
                    Amount = amount
                },
                ClaimDetails = new ChainSwapData
                {
                    LockupAddress = GenerateRegTestBtcAddress(),
                    ServerPublicKey = boltzPubHex,
                    TimeoutBlockHeight = 144,
                    Amount = amount - 500
                }
            };
        }
        else
        {
            // BTC→ARK: ARK-side address IS validated by the SDK — compute real VHTLC when possible.
            var amount = req.ServerLockAmount > 0 ? req.ServerLockAmount : 50_000;
            string arkClaimAddr;
            if (ServerInfo is not null && req.ClaimPublicKey is not null && req.PreimageHash is not null)
            {
                try { arkClaimAddr = ComputeBtcToArkVhtlcAddress(req, boltzKey, ServerInfo, timeouts); }
                catch { arkClaimAddr = FallbackArkAddress(); }
            }
            else
            {
                arkClaimAddr = FallbackArkAddress();
            }

            resp = new ChainResponse
            {
                Id = swapId,
                ClaimDetails = new ChainSwapData
                {
                    LockupAddress = arkClaimAddr,
                    ServerPublicKey = boltzPubHex,
                    TimeoutBlockHeight = timeouts.Refund,
                    Timeouts = timeouts,
                    Amount = amount
                },
                LockupDetails = new ChainSwapData
                {
                    LockupAddress = GenerateRegTestBtcAddress(),
                    ServerPublicKey = boltzPubHex,
                    TimeoutBlockHeight = 144,
                    Amount = amount + 500
                }
            };
        }

        _swaps[swapId] = new MockSwapState
        {
            Id = swapId,
            Status = "swap.created",
            ExpectedAmount = resp.LockupDetails?.Amount ?? 50_000
        };
        return Results.Json(resp, JsonOpts);
    }

    // ── WebSocket ────────────────────────────────────────────────────

    private async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new WsSession(ws);

        await _sessionsLock.WaitAsync();
        try { _sessions.Add(session); }
        finally { _sessionsLock.Release(); }

        try
        {
            await RunSessionAsync(session, ctx.RequestAborted);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* client disconnected */ }
        finally
        {
            await _sessionsLock.WaitAsync();
            try { _sessions.Remove(session); }
            finally { _sessionsLock.Release(); }
        }
    }

    private static async Task RunSessionAsync(WsSession session, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (session.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await session.Socket.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    return;
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            if (ms.Length == 0) continue;
            ms.Seek(0, SeekOrigin.Begin);

            JsonObject? node;
            try { node = JsonSerializer.Deserialize<JsonObject>(ms); }
            catch { continue; }
            if (node is null) continue;

            var op = node["op"]?.GetValue<string>();
            var channel = node["channel"]?.GetValue<string>();
            var args = node["args"]?.AsArray();

            if (op is not ("subscribe" or "unsubscribe") || channel != "swap.update" || args is null)
                continue;

            foreach (var a in args)
            {
                var id = a?.GetValue<string>();
                if (id is null) continue;
                if (op == "subscribe") session.Subscribe(id);
                else session.Unsubscribe(id);
            }

            // Confirm the subscribe/unsubscribe as the real Boltz does.
            var confirm = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = op,
                channel = "swap.update",
                args = new[] { new { status = "ok" } }
            });
            if (session.Socket.State == WebSocketState.Open)
                await session.Socket.SendAsync(confirm, WebSocketMessageType.Text, true, ct);
        }
    }

    // ── Admin API (called by tests) ──────────────────────────────────

    /// <summary>
    /// Updates the status returned by <c>GET /v2/swap/{id}</c> without pushing a WebSocket event.
    /// </summary>
    public void SetSwapStatus(string swapId, string status)
        => _swaps.AddOrUpdate(
            swapId,
            _ => new MockSwapState { Id = swapId, Status = status },
            (_, s) => { s.Status = status; return s; });

    /// <summary>
    /// Updates <c>GET /v2/swap/{id}</c> to include a lockup transaction hex in the response.
    /// Needed for tests that exercise the BTC-side refund path which reads <c>transaction.hex</c>
    /// from the Boltz status to locate the lockup outpoint.
    /// </summary>
    public void SetLockupTxHex(string swapId, string hex)
        => _swaps.AddOrUpdate(
            swapId,
            _ => new MockSwapState { Id = swapId, LockupTxHex = hex },
            (_, s) => { s.LockupTxHex = hex; return s; });

    /// <summary>
    /// Pushes a <c>swap.update</c> WebSocket event to all subscribed sessions and
    /// simultaneously updates the status returned by <c>GET /v2/swap/{id}</c>.
    /// This is the primary way tests drive the SDK's cooperative-refund / settlement code paths.
    /// </summary>
    public async Task PushSwapEvent(string swapId, string status, CancellationToken ct = default)
    {
        SetSwapStatus(swapId, status);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            @event = "update",
            channel = "swap.update",
            args = new[] { new { id = swapId, status } }
        });

        await _sessionsLock.WaitAsync(ct);
        List<WsSession> targets;
        try { targets = _sessions.Where(s => s.IsSubscribed(swapId)).ToList(); }
        finally { _sessionsLock.Release(); }

        foreach (var s in targets)
        {
            try
            {
                if (s.Socket.State == WebSocketState.Open)
                    await s.Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
            }
            catch { /* disconnected client — ignore */ }
        }
    }

    public void SetClaimMode(ClaimMode m) => Config.ClaimMode = m;
    public void SetRefundMode(RefundMode m) => Config.RefundMode = m;

    /// <summary>Number of cooperative refund requests received for a submarine swap.</summary>
    public int SubmarineRefundRequestsFor(string swapId)
        => _swaps.TryGetValue(swapId, out var s) ? s.SubmarineRefundCount : 0;

    /// <summary>Number of cooperative Arkade-side refund requests received for a chain swap.</summary>
    public int ChainArkRefundRequestsFor(string swapId)
        => _swaps.TryGetValue(swapId, out var s) ? s.ChainArkRefundCount : 0;

    /// <summary>Number of cooperative BTC-side refund requests received for a chain swap.</summary>
    public int ChainBtcRefundRequestsFor(string swapId)
        => _swaps.TryGetValue(swapId, out var s) ? s.ChainBtcRefundCount : 0;

    /// <summary>
    /// Total cooperative refund requests (submarine + chain Arkade-side).
    /// Matches the assertion pattern used in the doc example:
    /// <c>Assert.That(mockBoltz.RefundRequestsFor(swapId), Is.GreaterThan(0))</c>.
    /// </summary>
    public int RefundRequestsFor(string swapId)
        => SubmarineRefundRequestsFor(swapId) + ChainArkRefundRequestsFor(swapId);

    /// <summary>Clears all tracked swap states (does not disconnect active WebSocket sessions).</summary>
    public void Reset() => _swaps.Clear();

    // ── VHTLC address computation ────────────────────────────────────

    private static string ComputeSubmarineVhtlcAddress(
        SubmarineRequest req, Key boltzClaimKey, ArkServerInfo serverInfo, TimeoutBlockHeights t)
    {
        var invoice = BTCPayServer.Lightning.BOLT11PaymentRequest.Parse(req.Invoice, serverInfo.Network);
        // Payment hash = SHA256(preimage); VHTLC hash = RIPEMD160(SHA256(preimage))
        var hash = new uint160(Hashes.RIPEMD160(invoice.PaymentHash!.ToBytes(false)), false);

        var userDesc = ParseDescriptor(req.RefundPublicKey, serverInfo.Network);
        var boltzDesc = ParseDescriptor(Hex(boltzClaimKey.PubKey.ToBytes()), serverInfo.Network);

        return BuildVhtlc(serverInfo, userDesc, boltzDesc, hash, t)
            .GetArkAddress()
            .ToString(serverInfo.Network.ChainName == ChainName.Mainnet);
    }

    private static string ComputeReverseVhtlcAddress(
        ReverseRequest req, Key boltzRefundKey, ArkServerInfo serverInfo, TimeoutBlockHeights t)
    {
        // req.PreimageHash = hex(SHA256(preimage)); VHTLC hash = RIPEMD160(SHA256(preimage))
        var hash = new uint160(Hashes.RIPEMD160(Convert.FromHexString(req.PreimageHash)), false);

        var userDesc = ParseDescriptor(req.ClaimPublicKey!, serverInfo.Network);
        var boltzDesc = ParseDescriptor(Hex(boltzRefundKey.PubKey.ToBytes()), serverInfo.Network);

        // Reverse swap: Boltz is the sender, user is the receiver
        return BuildVhtlc(serverInfo, boltzDesc, userDesc, hash, t)
            .GetArkAddress()
            .ToString(serverInfo.Network.ChainName == ChainName.Mainnet);
    }

    private static string ComputeBtcToArkVhtlcAddress(
        ChainRequest req, Key boltzSenderKey, ArkServerInfo serverInfo, TimeoutBlockHeights t)
    {
        var hash = new uint160(Hashes.RIPEMD160(Convert.FromHexString(req.PreimageHash)), false);

        var userDesc = ParseDescriptor(req.ClaimPublicKey!, serverInfo.Network);
        var boltzDesc = ParseDescriptor(Hex(boltzSenderKey.PubKey.ToBytes()), serverInfo.Network);

        // BTC→ARK: Boltz (fulmine) is the sender, user is the receiver
        return BuildVhtlc(serverInfo, boltzDesc, userDesc, hash, t)
            .GetArkAddress()
            .ToString(serverInfo.Network.ChainName == ChainName.Mainnet);
    }

    private static VHTLCContract BuildVhtlc(
        ArkServerInfo serverInfo,
        OutputDescriptor sender,
        OutputDescriptor receiver,
        uint160 hash,
        TimeoutBlockHeights t)
    {
        return new VHTLCContract(
            server: serverInfo.SignerKey,
            sender: sender,
            receiver: receiver,
            hash: hash,
            refundLocktime: new LockTime(t.Refund),
            unilateralClaimDelay: new Sequence(t.UnilateralClaim),
            unilateralRefundDelay: new Sequence(t.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: new Sequence(t.UnilateralRefundWithoutReceiver));
    }

    // ── Utilities ────────────────────────────────────────────────────

    /// <summary>Replicates the NArk.Swaps-internal KeyExtensions.ParseOutputDescriptor logic.</summary>
    private static OutputDescriptor ParseDescriptor(string pubKeyHex, Network network)
    {
        var bytes = Convert.FromHexString(pubKeyHex);
        if (bytes.Length != 32 && bytes.Length != 33)
            throw new ArgumentException($"Expected 32 or 33 byte pubkey hex, got {bytes.Length}", nameof(pubKeyHex));
        return OutputDescriptor.Parse($"tr({pubKeyHex})", network);
    }

    /// <summary>Generates a random syntactically-valid ARK address (testnet/regtest).</summary>
    private static string FallbackArkAddress()
    {
        // ECXOnlyPubKey requires 32 bytes of valid x-coordinate; a compressed EC pubkey's
        // trailing 32 bytes are always a valid x-coordinate.
        var serverKey = ECXOnlyPubKey.Create(new Key().PubKey.ToBytes()[1..]);
        var tweakedKey = ECXOnlyPubKey.Create(new Key().PubKey.ToBytes()[1..]);
        return new ArkAddress(tweakedKey, serverKey, version: 0, isMainnet: false).ToString(isMainnet: false);
    }

    private static string GenerateRegTestBtcAddress()
        => new Key().PubKey.WitHash.GetAddress(Network.RegTest).ToString();

    private static TimeoutBlockHeights DefaultTimeouts() => new()
    {
        Refund = 1000,
        UnilateralClaim = 288,
        UnilateralRefund = 288,
        UnilateralRefundWithoutReceiver = 576
    };

    private static string NewSwapId() => Guid.NewGuid().ToString("N")[..12];

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static string RandHex(int len) => Hex(RandomUtils.GetBytes(len));

    private static object BuildSubmarinePairs() => new
    {
        ARK = new
        {
            BTC = new
            {
                fees = new { percentage = 0.1m, minerFees = 0 },
                limits = new { minimal = 1_000L, maximal = 25_000_000L, maximalZeroConf = 0L }
            }
        }
    };

    private static object BuildReversePairs() => new
    {
        BTC = new
        {
            ARK = new
            {
                fees = new { percentage = 0.5m, minerFees = 0 },
                limits = new { minimal = 1_000L, maximal = 25_000_000L }
            }
        }
    };

    private static object BuildChainPairs() => new
    {
        ARK = new
        {
            BTC = new
            {
                fees = new { percentage = 0.1m, minerFees = 0 },
                limits = new { minimal = 1_000L, maximal = 25_000_000L }
            }
        },
        BTC = new
        {
            ARK = new
            {
                fees = new { percentage = 0.1m, minerFees = 0 },
                limits = new { minimal = 1_000L, maximal = 25_000_000L }
            }
        }
    };

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

// ── Supporting internal types ────────────────────────────────────────

/// <summary>Mutable state for a single swap tracked by the mock.</summary>
internal sealed class MockSwapState
{
    public required string Id { get; init; }
    public string Status { get; set; } = "swap.created";
    public string? LockupTxHex { get; set; }
    public long ExpectedAmount { get; set; }

    public int SubmarineRefundCount;
    public int ChainArkRefundCount;
    public int ChainBtcRefundCount;
    public int ClaimCount;
}

/// <summary>Tracks one connected WebSocket client and its subscribed swap IDs.</summary>
internal sealed class WsSession(WebSocket socket)
{
    public WebSocket Socket { get; } = socket;

    private readonly HashSet<string> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void Subscribe(string swapId) { lock (_lock) _subscriptions.Add(swapId); }
    public void Unsubscribe(string swapId) { lock (_lock) _subscriptions.Remove(swapId); }
    public bool IsSubscribed(string swapId) { lock (_lock) return _subscriptions.Contains(swapId); }
}

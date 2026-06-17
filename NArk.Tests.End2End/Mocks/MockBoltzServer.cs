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
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Common;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Tests.End2End.Mocks;

public enum ClaimMode { Normal, Fail }
public enum RefundMode { Normal, Fail }

public sealed class MockBoltzConfig
{
    public ClaimMode ClaimMode { get; set; } = ClaimMode.Normal;
    public RefundMode RefundMode { get; set; } = RefundMode.Normal;
}

/// <summary>
/// In-process HTTP + WebSocket mock of the Boltz swap API.
/// Performs real cryptography: MuSig2 partial signatures for BTC claim/refund and
/// Schnorr script-path signing of Arkade PSBTs — matching Fulmine's Go mock server.
/// </summary>
public sealed class MockBoltzServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, MockSwapState> _swaps = new();
    private readonly List<WsSession> _sessions = new();
    private readonly SemaphoreSlim _sessionsLock = new(1, 1);

    private readonly Key _serverKey = new();
    private readonly ECPrivKey _serverEcKey;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string BaseUrl { get; }
    public string WsBaseUrl { get; }
    public MockBoltzConfig Config { get; } = new();
    public ArkServerInfo? ServerInfo { get; set; }

    private MockBoltzServer(WebApplication app, int port)
    {
        _app = app;
        BaseUrl = $"http://127.0.0.1:{port}";
        WsBaseUrl = $"ws://127.0.0.1:{port}";
        _serverEcKey = ECPrivKey.Create(_serverKey.ToBytes());
    }

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

    private void RegisterRoutes(WebApplication app)
    {
        app.MapGet("/version", () => Results.Ok(new { version = "mock-boltz" }));
        app.MapGet("/v2/swap/submarine", () => Results.Json(BuildSubmarinePairs(), JsonOpts));
        app.MapGet("/v2/swap/reverse",   () => Results.Json(BuildReversePairs(),   JsonOpts));
        app.MapGet("/v2/swap/chain",     () => Results.Json(BuildChainPairs(),     JsonOpts));

        app.MapGet("/v2/swap/{swapId}", (string swapId) =>
        {
            if (_swaps.TryGetValue(swapId, out var s))
                return Results.Json(new SwapStatusResponse
                {
                    Status = s.Status,
                    Transaction = s.LockupTxHex is not null
                        ? new SwapStatusTransaction { Hex = s.LockupTxHex } : null
                }, JsonOpts);
            return Results.NotFound(new { error = $"could not find swap with id {swapId}" });
        });

        app.MapPost("/v2/swap/submarine", (HttpRequest req) => HandleCreateSubmarineAsync(req));

        app.MapPost("/v2/swap/submarine/{swapId}/refund/ark", async (string swapId, HttpRequest req) =>
        {
            if (_swaps.TryGetValue(swapId, out var s)) Interlocked.Increment(ref s.SubmarineRefundCount);
            if (Config.RefundMode == RefundMode.Fail)
                return Results.BadRequest(new { error = "refund refused" });
            var body = await req.ReadFromJsonAsync<SubmarineRefundRequest>(JsonOpts);
            if (body is null) return Results.BadRequest();
            try
            {
                return Results.Json(new SubmarineRefundResponse
                {
                    Transaction = SignTaprootPsbt(body.Transaction),
                    Checkpoint  = SignTaprootPsbt(body.Checkpoint)
                }, JsonOpts);
            }
            catch
            {
                return Results.Json(new SubmarineRefundResponse
                    { Transaction = body.Transaction, Checkpoint = body.Checkpoint }, JsonOpts);
            }
        });

        app.MapPost("/v2/swap/reverse", (HttpRequest req) => HandleCreateReverseAsync(req));
        app.MapPost("/v2/swap/chain",   (HttpRequest req) => HandleCreateChainAsync(req));

        // GET /chain/{id}/claim — real server nonce for MuSig2
        app.MapGet("/v2/swap/chain/{swapId}/claim", (string swapId) =>
        {
            if (!_swaps.TryGetValue(swapId, out var st))
                return Results.NotFound(new { error = $"could not find swap with id {swapId}" });
            var serverEcPub = _serverEcKey.CreatePubKey();
            var clientKey   = st.ClaimEcPubKey ?? serverEcPub;
            var ctx   = new MusigContext([serverEcPub, clientKey], new byte[32], serverEcPub);
            var nonce = ctx.GenerateNonce(_serverEcKey);
            return Results.Json(new ChainClaimDetails
            {
                PubNonce        = Hex(nonce.CreatePubNonce().ToBytes()),
                PublicKey       = Hex(serverEcPub.ToBytes()),
                TransactionHash = new string('0', 64)
            }, JsonOpts);
        });

        // POST /chain/{id}/claim — real MuSig2 partial sig
        app.MapPost("/v2/swap/chain/{swapId}/claim", async (string swapId, HttpRequest req) =>
        {
            if (_swaps.TryGetValue(swapId, out var s)) Interlocked.Increment(ref s.ClaimCount);
            if (Config.ClaimMode == ClaimMode.Fail)
                return Results.BadRequest(new { error = "claim refused" });
            var body = await req.ReadFromJsonAsync<ChainClaimRequest>(JsonOpts);
            if (body is null) return Results.BadRequest();
            // Cross-sign only (no ToSign) — BTC→ARK path; mock ignores cross-sig
            if (body.ToSign is null)
                return Results.Json(
                    new PartialSignatureData { PubNonce = RandHex(66), PartialSignature = RandHex(32) }, JsonOpts);
            if (!_swaps.TryGetValue(swapId, out var st) || st.BtcLockupScript is null || st.ClaimEcPubKey is null)
                return Results.NotFound();
            try
            {
                return Results.Json(
                    MakeMusig2PartialSig(st, body.ToSign.PubNonce, body.ToSign.Transaction,
                        body.ToSign.Index, clientIsClaimKey: true), JsonOpts);
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
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

        // POST /chain/{id}/refund — BTC-side MuSig2 cooperative refund
        app.MapPost("/v2/swap/chain/{swapId}/refund", async (string swapId, HttpRequest req) =>
        {
            if (_swaps.TryGetValue(swapId, out var s)) Interlocked.Increment(ref s.ChainBtcRefundCount);
            if (Config.RefundMode == RefundMode.Fail)
                return Results.BadRequest(new { error = "refund refused" });
            var body = await req.ReadFromJsonAsync<ChainRefundRequest>(JsonOpts);
            if (body is null) return Results.BadRequest();
            if (!_swaps.TryGetValue(swapId, out var st) || st.BtcLockupScript is null || st.RefundEcPubKey is null)
                return Results.NotFound();
            try
            {
                return Results.Json(
                    MakeMusig2PartialSig(st, body.PubNonce, body.Transaction, body.Index,
                        clientIsClaimKey: false), JsonOpts);
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // POST /chain/{id}/refund/ark — ARK-side PSBT signing
        app.MapPost("/v2/swap/chain/{swapId}/refund/ark", async (string swapId, HttpRequest req) =>
        {
            if (_swaps.TryGetValue(swapId, out var s)) Interlocked.Increment(ref s.ChainArkRefundCount);
            if (Config.RefundMode == RefundMode.Fail)
                return Results.BadRequest(new { error = "refund refused" });
            var body = await req.ReadFromJsonAsync<ChainArkRefundRequest>(JsonOpts);
            if (body is null) return Results.BadRequest();
            try
            {
                return Results.Json(new ChainArkRefundResponse
                {
                    Transaction = SignTaprootPsbt(body.Transaction),
                    Checkpoint  = SignTaprootPsbt(body.Checkpoint)
                }, JsonOpts);
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/v2/chain/BTC/transaction",
            () => Results.Json(new BroadcastResponse { Id = RandHex(32) }, JsonOpts));
        app.MapPost("/v2/swap/restore",
            () => Results.Ok(Array.Empty<object>()));
        app.Map("/v2/ws", HandleWebSocketAsync);
    }

    // ── Real MuSig2 signing ──────────────────────────────────────────

    private PartialSignatureData MakeMusig2PartialSig(
        MockSwapState st, string clientPubNonceHex, string unsignedTxHex, int inputIndex,
        bool clientIsClaimKey)
    {
        var clientEcPub = clientIsClaimKey ? st.ClaimEcPubKey! : st.RefundEcPubKey!;
        var serverEcPub = _serverEcKey.CreatePubKey();

        var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
            st.BtcSwapTree!, clientEcPub, serverEcPub);

        var tx      = Transaction.Parse(unsignedTxHex, Network.RegTest);
        var prevOut = new TxOut(Money.Satoshis(st.BtcLockupAmount), st.BtcLockupScript!);
        var sighash = BtcTransactionBuilder.ComputeKeyPathSighash(tx, inputIndex, [prevOut]);

        // [serverKey, clientKey] — Boltz ordering
        var ctx = new MusigContext([serverEcPub, clientEcPub], sighash.ToBytes(), serverEcPub);
        ApplyTaprootTweak(ctx, spendInfo);

        var serverNonce = ctx.GenerateNonce(_serverEcKey);
        var clientNonce = new MusigPubNonce(Convert.FromHexString(clientPubNonceHex));
        ctx.ProcessNonces([serverNonce.CreatePubNonce(), clientNonce]);
        var partialSig = ctx.Sign(_serverEcKey, serverNonce);

        return new PartialSignatureData
        {
            PubNonce        = Hex(serverNonce.CreatePubNonce().ToBytes()),
            PartialSignature = Hex(partialSig.ToBytes())
        };
    }

    /// <summary>
    /// Signs every Taproot script-path input in the PSBT using the server key.
    /// Mirrors Fulmine's Go <c>signCollaborativeRefundPSBT</c>.
    /// </summary>
    private string SignTaprootPsbt(string base64Psbt)
    {
        var psbt      = PSBT.Parse(base64Psbt, Network.RegTest);
        var tx        = psbt.GetGlobalTransaction();
        var serverXOnly = _serverEcKey.CreatePubKey().ToXOnlyPubKey();

        var prevOuts = psbt.Inputs
            .Select(i => i.WitnessUtxo ?? new TxOut(Money.Zero, Script.Empty))
            .ToArray();
        var precomputed = tx.PrecomputeTransactionData(prevOuts);

        const byte TapLeafPrefix = 0x15;

        for (var i = 0; i < psbt.Inputs.Count; i++)
        {
            var input = psbt.Inputs[i];
            if (input.WitnessUtxo is null) continue;

            foreach (var (keyBytes, valueBytes) in input.Unknown)
            {
                if (keyBytes.Length == 0 || keyBytes[0] != TapLeafPrefix) continue;
                if (valueBytes.Length < 2) continue;

                var leafVersion = (TapLeafVersion)valueBytes[^1];
                var script      = Script.FromBytesUnsafe(valueBytes[..^1]);
                var leaf        = new TapScript(script, leafVersion);

                var execData = new TaprootExecutionData(i, leaf.LeafHash)
                {
                    SigHash = TaprootSigHash.Default
                };
                var sighash = tx.GetSignatureHashTaproot(precomputed, execData);
                var tapSig  = _serverKey.SignTaprootScriptSpend(sighash, TaprootSigHash.Default);

                var sigBytes = tapSig.ToBytes();
                if (SecpSchnorrSignature.TryCreate(sigBytes[..Math.Min(64, sigBytes.Length)], out var sig) && sig is not null)
                    input.SetTaprootScriptSpendSignature(serverXOnly, leaf.LeafHash, sig);
            }
        }

        return psbt.ToBase64();
    }

    private static void ApplyTaprootTweak(MusigContext ctx, TaprootSpendInfo spendInfo)
    {
        var merkleRoot = spendInfo.MerkleRoot;
        if (merkleRoot is null) return;
        using var sha = new NBitcoin.Secp256k1.SHA256();
        sha.InitializeTagged("TapTweak");
        sha.Write(ctx.AggregatePubKey.ToXOnlyPubKey().ToBytes());
        sha.Write(merkleRoot.ToBytes());
        ctx.Tweak(sha.GetHash());
    }

    // ── Real BTC HTLC construction ───────────────────────────────────

    private (string Address, Script PkScript, ChainSwapTree SwapTree,
             ECPubKey ClaimEcPub, ECPubKey RefundEcPub)
        BuildBtcHtlc(string claimPubHex, string refundPubHex, byte[] hash160,
                     uint timeout, bool serverIsClaimer)
    {
        ECPubKey claimEcPub, refundEcPub, musigClientKey;
        if (serverIsClaimer)
        {
            claimEcPub     = _serverEcKey.CreatePubKey();
            refundEcPub    = ECPubKey.Create(Convert.FromHexString(refundPubHex));
            musigClientKey = refundEcPub;
        }
        else
        {
            claimEcPub     = ECPubKey.Create(Convert.FromHexString(claimPubHex));
            refundEcPub    = _serverEcKey.CreatePubKey();
            musigClientKey = claimEcPub;
        }

        var claimScript  = BuildClaimScript(hash160, claimEcPub.ToXOnlyPubKey().ToBytes());
        var refundScript = BuildRefundScript(refundEcPub.ToXOnlyPubKey().ToBytes(), timeout);

        var swapTree = new ChainSwapTree
        {
            ClaimLeaf  = new ChainSwapTreeLeaf { Version = 0xC0, Output = claimScript.ToHex() },
            RefundLeaf = new ChainSwapTreeLeaf { Version = 0xC0, Output = refundScript.ToHex() }
        };

        // MuSig2 internal key: [serverKey, clientKey] — Boltz ordering
        var aggregateXOnly = ECPubKey.MusigAggregate([_serverEcKey.CreatePubKey(), musigClientKey]).ToXOnlyPubKey();
        var internalKey    = new TaprootInternalPubKey(aggregateXOnly.ToBytes());
        var claimLeaf      = new TapScript(claimScript,  TapLeafVersion.C0);
        var refundLeaf     = new TapScript(refundScript, TapLeafVersion.C0);
        var spendInfo      = TaprootSpendInfo.FromNodeInfo(internalKey, new[] { claimLeaf, refundLeaf }.BuildTree());

        var pkScript = spendInfo.OutputPubKey.ScriptPubKey;
        var address  = pkScript.GetDestinationAddress(Network.RegTest)!.ToString();
        return (address, pkScript, swapTree, claimEcPub, refundEcPub);
    }

    // OP_SIZE 32 OP_EQUALVERIFY OP_HASH160 <hash160> OP_EQUALVERIFY <claimKey> OP_CHECKSIG
    private static Script BuildClaimScript(byte[] hash160, byte[] claimKeyXOnly) =>
        new(OpcodeType.OP_SIZE, Op.GetPushOp(new byte[] { 0x20 }), OpcodeType.OP_EQUALVERIFY,
            OpcodeType.OP_HASH160, Op.GetPushOp(hash160), OpcodeType.OP_EQUALVERIFY,
            Op.GetPushOp(claimKeyXOnly), OpcodeType.OP_CHECKSIG);

    // <refundKey> OP_CHECKSIGVERIFY <timeout> OP_CHECKLOCKTIMEVERIFY
    private static Script BuildRefundScript(byte[] refundKeyXOnly, uint timeout) =>
        new(Op.GetPushOp(refundKeyXOnly), OpcodeType.OP_CHECKSIGVERIFY,
            Op.GetPushOp((long)timeout), OpcodeType.OP_CHECKLOCKTIMEVERIFY);

    // ── Swap creation ────────────────────────────────────────────────

    private async Task<IResult> HandleCreateSubmarineAsync(HttpRequest httpReq)
    {
        var req = await httpReq.ReadFromJsonAsync<SubmarineRequest>(JsonOpts);
        if (req is null) return Results.BadRequest();
        var swapId  = NewSwapId();
        // Use the persistent server key as the Boltz claim key so that
        // SignTaprootPsbt (which signs with _serverKey) produces a valid
        // co-sign when the SDK calls the refund endpoint. This mirrors
        // fulmine's mock which uses s.privateKey for every swap role.
        var boltzKey = _serverKey;
        var timeouts = DefaultTimeouts();
        string address;
        if (ServerInfo is not null) { try { address = ComputeSubmarineVhtlcAddress(req, boltzKey, ServerInfo, timeouts); } catch { address = FallbackArkAddress(); } }
        else { address = FallbackArkAddress(); }
        _swaps[swapId] = new MockSwapState { Id = swapId, Status = "swap.created", ExpectedAmount = 50_000 };
        return Results.Json(new SubmarineResponse
        {
            Id = swapId, Address = address, ExpectedAmount = 50_000,
            ClaimPublicKey = Hex(boltzKey.PubKey.ToBytes()), AcceptZeroConf = true,
            TimeoutBlockHeights = timeouts
        }, JsonOpts);
    }

    private async Task<IResult> HandleCreateReverseAsync(HttpRequest httpReq)
    {
        var req = await httpReq.ReadFromJsonAsync<ReverseRequest>(JsonOpts);
        if (req is null) return Results.BadRequest();
        var swapId  = NewSwapId();
        var boltzKey = new Key();
        var timeouts = DefaultTimeouts();
        var amount   = req.OnchainAmount ?? req.InvoiceAmount ?? 50_000;
        string lockupAddress;
        if (ServerInfo is not null && req.ClaimPublicKey is not null && req.PreimageHash is not null)
        { try { lockupAddress = ComputeReverseVhtlcAddress(req, boltzKey, ServerInfo, timeouts); } catch { lockupAddress = FallbackArkAddress(); } }
        else { lockupAddress = FallbackArkAddress(); }
        _swaps[swapId] = new MockSwapState { Id = swapId, Status = "swap.created", ExpectedAmount = amount };
        return Results.Json(new ReverseResponse
        {
            Id = swapId, LockupAddress = lockupAddress,
            RefundPublicKey = Hex(boltzKey.PubKey.ToBytes()),
            TimeoutBlockHeights = timeouts,
            Invoice = $"lnbcrt{amount}n1mockbolt11{swapId}"
        }, JsonOpts);
    }

    private async Task<IResult> HandleCreateChainAsync(HttpRequest httpReq)
    {
        var req = await httpReq.ReadFromJsonAsync<ChainRequest>(JsonOpts);
        if (req is null) return Results.BadRequest();

        var swapId      = NewSwapId();
        var serverPubHex = Hex(_serverKey.PubKey.ToBytes());
        var timeouts     = DefaultTimeouts();
        const uint btcTimeout = 144;

        var preimageHashBytes = Convert.FromHexString(req.PreimageHash ?? new string('0', 64));
        var hash160 = preimageHashBytes.Length == 32
            ? Hashes.RIPEMD160(preimageHashBytes) : preimageHashBytes;

        ChainResponse resp;
        MockSwapState state;

        if (req.From == "ARK")
        {
            var amount       = req.UserLockAmount > 0 ? req.UserLockAmount : 50_000;
            var serverAmount = amount - 500;
            // Use a low refund locktime so tests can mine past it and trigger
            // the refundWithoutReceiver batch path without Boltz co-sign.
            timeouts.Refund = 2;
            var (btcAddr, btcScript, swapTree, claimEcPub, _) =
                BuildBtcHtlc(req.ClaimPublicKey  ?? serverPubHex,
                             req.RefundPublicKey ?? serverPubHex,
                             hash160, btcTimeout, serverIsClaimer: false);
            resp = new ChainResponse
            {
                Id = swapId,
                ClaimDetails = new ChainSwapData
                {
                    LockupAddress = btcAddr, ServerPublicKey = serverPubHex,
                    TimeoutBlockHeight = (int)btcTimeout, SwapTree = swapTree,
                    Amount = (int)serverAmount
                },
                LockupDetails = new ChainSwapData
                {
                    LockupAddress = FallbackArkAddress(), ServerPublicKey = serverPubHex,
                    TimeoutBlockHeight = timeouts.Refund, Timeouts = timeouts, Amount = (int)amount
                }
            };
            state = new MockSwapState
            {
                Id = swapId, Status = "swap.created", ExpectedAmount = amount,
                ClaimEcPubKey = claimEcPub, BtcLockupScript = btcScript,
                BtcLockupAmount = serverAmount, BtcSwapTree = swapTree
            };
        }
        else
        {
            var amount        = req.ServerLockAmount > 0 ? req.ServerLockAmount : 50_000;
            var userLockAmount = amount + 500;
            var (btcAddr, btcScript, swapTree, _, refundEcPub) =
                BuildBtcHtlc(req.ClaimPublicKey  ?? serverPubHex,
                             req.RefundPublicKey ?? serverPubHex,
                             hash160, btcTimeout, serverIsClaimer: true);
            string arkClaimAddr;
            if (ServerInfo is not null && req.ClaimPublicKey is not null && req.PreimageHash is not null)
            { try { arkClaimAddr = ComputeBtcToArkVhtlcAddress(req, _serverKey, ServerInfo, timeouts); } catch { arkClaimAddr = FallbackArkAddress(); } }
            else { arkClaimAddr = FallbackArkAddress(); }
            resp = new ChainResponse
            {
                Id = swapId,
                ClaimDetails = new ChainSwapData
                {
                    LockupAddress = arkClaimAddr, ServerPublicKey = serverPubHex,
                    TimeoutBlockHeight = timeouts.Refund, Timeouts = timeouts, Amount = (int)amount
                },
                LockupDetails = new ChainSwapData
                {
                    LockupAddress = btcAddr, ServerPublicKey = serverPubHex,
                    TimeoutBlockHeight = (int)btcTimeout, SwapTree = swapTree,
                    Amount = (int)userLockAmount
                }
            };
            state = new MockSwapState
            {
                Id = swapId, Status = "swap.created", ExpectedAmount = userLockAmount,
                RefundEcPubKey = refundEcPub, BtcLockupScript = btcScript,
                BtcLockupAmount = userLockAmount, BtcSwapTree = swapTree
            };
        }

        _swaps[swapId] = state;
        return Results.Json(resp, JsonOpts);
    }

    // ── WebSocket ────────────────────────────────────────────────────

    private async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new WsSession(ws);
        await _sessionsLock.WaitAsync();
        try { _sessions.Add(session); } finally { _sessionsLock.Release(); }
        try { await RunSessionAsync(session, ctx.RequestAborted); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            await _sessionsLock.WaitAsync();
            try { _sessions.Remove(session); } finally { _sessionsLock.Release(); }
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
                if (res.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);
            if (ms.Length == 0) continue;
            ms.Seek(0, SeekOrigin.Begin);
            JsonObject? node;
            try { node = JsonSerializer.Deserialize<JsonObject>(ms); } catch { continue; }
            if (node is null) continue;
            var op      = node["op"]?.GetValue<string>();
            var channel = node["channel"]?.GetValue<string>();
            var args    = node["args"]?.AsArray();
            if (op is not ("subscribe" or "unsubscribe") || channel != "swap.update" || args is null) continue;
            foreach (var a in args)
            {
                var id = a?.GetValue<string>();
                if (id is null) continue;
                if (op == "subscribe") session.Subscribe(id); else session.Unsubscribe(id);
            }
            var confirm = JsonSerializer.SerializeToUtf8Bytes(new
                { @event = op, channel = "swap.update", args = new[] { new { status = "ok" } } });
            if (session.Socket.State == WebSocketState.Open)
                await session.Socket.SendAsync(confirm, WebSocketMessageType.Text, true, ct);
        }
    }

    // ── Admin / test control ─────────────────────────────────────────

    public void SetSwapStatus(string swapId, string status)
        => _swaps.AddOrUpdate(swapId,
            _ => new MockSwapState { Id = swapId, Status = status },
            (_, s) => { s.Status = status; return s; });

    public void SetLockupTxHex(string swapId, string hex)
        => _swaps.AddOrUpdate(swapId,
            _ => new MockSwapState { Id = swapId, LockupTxHex = hex },
            (_, s) => { s.LockupTxHex = hex; return s; });

    public async Task PushSwapEvent(string swapId, string status, CancellationToken ct = default)
    {
        SetSwapStatus(swapId, status);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
            { @event = "update", channel = "swap.update",
              args = new[] { new { id = swapId, status } } });
        await _sessionsLock.WaitAsync(ct);
        List<WsSession> targets;
        try { targets = _sessions.Where(s => s.IsSubscribed(swapId)).ToList(); }
        finally { _sessionsLock.Release(); }
        foreach (var s in targets)
        {
            try { if (s.Socket.State == WebSocketState.Open)
                      await s.Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct); }
            catch { }
        }
    }

    public void SetClaimMode(ClaimMode m)  => Config.ClaimMode  = m;
    public void SetRefundMode(RefundMode m) => Config.RefundMode = m;

    public int SubmarineRefundRequestsFor(string swapId)
        => _swaps.TryGetValue(swapId, out var s) ? s.SubmarineRefundCount : 0;
    public int ChainArkRefundRequestsFor(string swapId)
        => _swaps.TryGetValue(swapId, out var s) ? s.ChainArkRefundCount : 0;
    public int ChainBtcRefundRequestsFor(string swapId)
        => _swaps.TryGetValue(swapId, out var s) ? s.ChainBtcRefundCount : 0;
    public int RefundRequestsFor(string swapId)
        => SubmarineRefundRequestsFor(swapId) + ChainArkRefundRequestsFor(swapId);
    public void Reset() => _swaps.Clear();

    // ── VHTLC address helpers (ARK side) ────────────────────────────

    private static string ComputeSubmarineVhtlcAddress(
        SubmarineRequest req, Key boltzClaimKey, ArkServerInfo serverInfo, TimeoutBlockHeights t)
    {
        var invoice = BTCPayServer.Lightning.BOLT11PaymentRequest.Parse(req.Invoice, serverInfo.Network);
        var hash = new uint160(Hashes.RIPEMD160(invoice.PaymentHash!.ToBytes(false)), false);
        var userDesc  = ParseDescriptor(req.RefundPublicKey, serverInfo.Network);
        var boltzDesc = ParseDescriptor(Hex(boltzClaimKey.PubKey.ToBytes()), serverInfo.Network);
        return BuildVhtlc(serverInfo, userDesc, boltzDesc, hash, t)
            .GetArkAddress().ToString(serverInfo.Network.ChainName == ChainName.Mainnet);
    }

    private static string ComputeReverseVhtlcAddress(
        ReverseRequest req, Key boltzRefundKey, ArkServerInfo serverInfo, TimeoutBlockHeights t)
    {
        var hash = new uint160(Hashes.RIPEMD160(Convert.FromHexString(req.PreimageHash)), false);
        var userDesc  = ParseDescriptor(req.ClaimPublicKey!, serverInfo.Network);
        var boltzDesc = ParseDescriptor(Hex(boltzRefundKey.PubKey.ToBytes()), serverInfo.Network);
        return BuildVhtlc(serverInfo, boltzDesc, userDesc, hash, t)
            .GetArkAddress().ToString(serverInfo.Network.ChainName == ChainName.Mainnet);
    }

    private static string ComputeBtcToArkVhtlcAddress(
        ChainRequest req, Key boltzSenderKey, ArkServerInfo serverInfo, TimeoutBlockHeights t)
    {
        var hash = new uint160(Hashes.RIPEMD160(Convert.FromHexString(req.PreimageHash)), false);
        var userDesc  = ParseDescriptor(req.ClaimPublicKey!, serverInfo.Network);
        var boltzDesc = ParseDescriptor(Hex(boltzSenderKey.PubKey.ToBytes()), serverInfo.Network);
        return BuildVhtlc(serverInfo, boltzDesc, userDesc, hash, t)
            .GetArkAddress().ToString(serverInfo.Network.ChainName == ChainName.Mainnet);
    }

    private static VHTLCContract BuildVhtlc(
        ArkServerInfo serverInfo, OutputDescriptor sender, OutputDescriptor receiver,
        uint160 hash, TimeoutBlockHeights t) =>
        new(server: serverInfo.SignerKey, sender: sender, receiver: receiver, hash: hash,
            refundLocktime: new LockTime(t.Refund),
            unilateralClaimDelay: ParseSequence(t.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(t.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(t.UnilateralRefundWithoutReceiver));

    // Mirrors BoltzSwapsService.ParseSequence: values >= 512 are treated as seconds
    // (time-based CSV), values < 512 are block counts. Must stay in sync with the
    // SDK so MockBoltz derives identical VHTLC scripts and addresses.
    private static Sequence ParseSequence(long val) =>
        val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);

    // ── Utilities ────────────────────────────────────────────────────

    private static OutputDescriptor ParseDescriptor(string pubKeyHex, Network network)
    {
        var bytes = Convert.FromHexString(pubKeyHex);
        if (bytes.Length != 32 && bytes.Length != 33)
            throw new ArgumentException($"Expected 32 or 33 byte pubkey, got {bytes.Length}");
        return OutputDescriptor.Parse($"tr({pubKeyHex})", network);
    }

    private static string FallbackArkAddress()
    {
        var serverKey  = ECXOnlyPubKey.Create(new Key().PubKey.ToBytes()[1..]);
        var tweakedKey = ECXOnlyPubKey.Create(new Key().PubKey.ToBytes()[1..]);
        return new ArkAddress(tweakedKey, serverKey, version: 0, isMainnet: false).ToString(isMainnet: false);
    }

    private static TimeoutBlockHeights DefaultTimeouts() => new()
    {
        Refund = 1000, UnilateralClaim = 288, UnilateralRefund = 288,
        UnilateralRefundWithoutReceiver = 576
    };

    private static string NewSwapId() => Guid.NewGuid().ToString("N")[..12];
    private static string Hex(byte[] b)  => Convert.ToHexString(b).ToLowerInvariant();
    private static string RandHex(int n) => Hex(RandomUtils.GetBytes(n));

    private static object BuildSubmarinePairs() => new { ARK = new { BTC = new {
        fees = new { percentage = 0.1m, minerFees = 0 },
        limits = new { minimal = 1_000L, maximal = 25_000_000L, maximalZeroConf = 0L } } } };

    private static object BuildReversePairs() => new { BTC = new { ARK = new {
        fees = new { percentage = 0.5m, minerFees = 0 },
        limits = new { minimal = 1_000L, maximal = 25_000_000L } } } };

    private static object BuildChainPairs() => new
    {
        ARK = new { BTC = new { fees = new { percentage = 0.1m, minerFees = 0 }, limits = new { minimal = 1_000L, maximal = 25_000_000L } } },
        BTC = new { ARK = new { fees = new { percentage = 0.1m, minerFees = 0 }, limits = new { minimal = 1_000L, maximal = 25_000_000L } } }
    };

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

internal sealed class MockSwapState
{
    public required string Id { get; init; }
    public string Status { get; set; } = "swap.created";
    public string? LockupTxHex { get; set; }
    public long ExpectedAmount { get; set; }

    public ECPubKey? ClaimEcPubKey  { get; set; }
    public ECPubKey? RefundEcPubKey { get; set; }
    public Script? BtcLockupScript  { get; set; }
    public long BtcLockupAmount     { get; set; }
    public ChainSwapTree? BtcSwapTree { get; set; }

    public int SubmarineRefundCount;
    public int ChainArkRefundCount;
    public int ChainBtcRefundCount;
    public int ClaimCount;
}

internal sealed class WsSession(WebSocket socket)
{
    public WebSocket Socket { get; } = socket;
    private readonly HashSet<string> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    public void Subscribe(string id)   { lock (_lock) _subscriptions.Add(id); }
    public void Unsubscribe(string id) { lock (_lock) _subscriptions.Remove(id); }
    public bool IsSubscribed(string id){ lock (_lock) return _subscriptions.Contains(id); }
}

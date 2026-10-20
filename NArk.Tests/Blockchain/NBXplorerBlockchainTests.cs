using System.Collections.Concurrent;
using System.Net;
using System.Text;
using NArk.Blockchain;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;
using Newtonsoft.Json.Linq;

namespace NArk.Tests.Blockchain;

[TestFixture]
public class NBXplorerBlockchainTests
{
    private const int TipHeight = 200;
    private static readonly DateTimeOffset FirstSeen = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private FakeNBXplorer _server = null!;
    private NBXplorerBlockchain _blockchain = null!;
    private string _address = null!;

    [SetUp]
    public void SetUp()
    {
        var client = new ExplorerClient(
            new NBXplorerNetworkProvider(ChainName.Regtest).GetBTC(), new Uri("http://localhost:39372"));
        _server = new FakeNBXplorer(client.Serializer);
        client.SetClient(new HttpClient(_server));
        client.RPCClient.HttpClient = new HttpClient(_server);
        _blockchain = new NBXplorerBlockchain(client);
        _address = new Key().GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();
    }

    [TearDown]
    public void TearDown() => _server.Dispose();

    [Test]
    public async Task GetUtxosAsync_ConfirmedUtxo_UsesBlockHeaderTime()
    {
        _server.SetBlock(TipHeight - 2, "aa", 1_600_000_123);
        _server.Utxos.Add(NewUtxo(confirmations: 3));

        var utxo = (await _blockchain.GetUtxosAsync(_address)).Single();

        Assert.That(utxo.BlockHeight, Is.EqualTo(TipHeight - 2));
        Assert.That(utxo.BlockTime, Is.EqualTo(1_600_000_123));
    }

    [Test]
    public async Task GetUtxosAsync_UnconfirmedUtxo_HasZeroBlockTimeAndSkipsHeaderLookup()
    {
        _server.SetBlock(0, "00", 1_231_006_505);
        _server.Utxos.Add(NewUtxo(confirmations: 0));

        var utxo = (await _blockchain.GetUtxosAsync(_address)).Single();

        Assert.That(utxo.BlockTime, Is.EqualTo(0));
        Assert.That(_server.RpcCalls, Is.Empty);
    }

    [Test]
    public async Task GetUtxosAsync_HeaderLookupFails_FallsBackToFirstSeenAndRetriesNextCall()
    {
        _server.Utxos.Add(NewUtxo(confirmations: 1));

        var first = (await _blockchain.GetUtxosAsync(_address)).Single();
        Assert.That(first.BlockTime, Is.EqualTo(FirstSeen.ToUnixTimeSeconds()));

        _server.SetBlock(TipHeight, "bb", 1_600_000_456);
        var second = (await _blockchain.GetUtxosAsync(_address)).Single();
        Assert.That(second.BlockTime, Is.EqualTo(1_600_000_456));
    }

    [Test]
    public async Task GetUtxosAsync_SameBlock_FetchesHeaderOnce()
    {
        _server.SetBlock(TipHeight, "cc", 1_600_000_789);
        _server.Utxos.Add(NewUtxo(confirmations: 1));
        _server.Utxos.Add(NewUtxo(confirmations: 1));

        await _blockchain.GetUtxosAsync(_address);
        await _blockchain.GetUtxosAsync(_address);

        Assert.That(_server.RpcCalls.Count(m => m == "getblockheader"), Is.EqualTo(1));
    }

    [Test]
    public async Task GetUtxosAsync_BlockReorgedAtSameHeight_RefetchesHeader()
    {
        _server.SetBlock(TipHeight, "dd", 1_600_000_001);
        _server.Utxos.Add(NewUtxo(confirmations: 1));
        await _blockchain.GetUtxosAsync(_address);

        _server.SetBlock(TipHeight, "ee", 1_600_000_002);
        var utxo = (await _blockchain.GetUtxosAsync(_address)).Single();

        Assert.That(utxo.BlockTime, Is.EqualTo(1_600_000_002));
    }

    [Test]
    public void GetMedianTimePastAsync_CancelledToken_Throws()
    {
        _server.SetBlock(TipHeight, "ff", 1_600_000_003);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(() => _blockchain.GetMedianTimePastAsync(TipHeight, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void GetUtxosAsync_CancelledDuringHeaderLookup_Throws()
    {
        _server.SetBlock(TipHeight, "ab", 1_600_000_004);
        _server.Utxos.Add(NewUtxo(confirmations: 1));
        using var cts = new CancellationTokenSource();
        _server.OnRpc = method => { if (method == "getblockhash") cts.Cancel(); };

        Assert.That(() => _blockchain.GetUtxosAsync(_address, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(_server.RpcCalls, Does.Not.Contain("getblockheader"));
    }

    private static UTXO NewUtxo(int confirmations) => new()
    {
        Outpoint = new OutPoint(RandomUtils.GetUInt256(), 0),
        Value = Money.Satoshis(10_000),
        ScriptPubKey = new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86),
        Confirmations = confirmations,
        Timestamp = FirstSeen,
    };

    private sealed class FakeNBXplorer(Serializer serializer) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<long, (string Hash, long Time)> _blocks = new();

        public List<UTXO> Utxos { get; } = [];
        public ConcurrentQueue<string> RpcCalls { get; } = new();
        public Action<string>? OnRpc { get; set; }

        public void SetBlock(long height, string hashByte, long time) =>
            _blocks[height] = (string.Concat(Enumerable.Repeat(hashByte, 32)), time);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/utxos"))
            {
                var changes = new UTXOChanges
                {
                    CurrentHeight = TipHeight,
                    Confirmed = new UTXOChange { UTXOs = Utxos.Where(u => u.Confirmations > 0).ToList() },
                    Unconfirmed = new UTXOChange { UTXOs = Utxos.Where(u => u.Confirmations == 0).ToList() },
                };
                return Json(serializer.ToString(changes));
            }
            if (!path.EndsWith("/rpc"))
                return new HttpResponseMessage(HttpStatusCode.OK);

            var rpc = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = (string)rpc["method"]!;
            var param = rpc["params"]![0]!;
            RpcCalls.Enqueue(method);
            OnRpc?.Invoke(method);

            JToken? result = method switch
            {
                "getblockhash" => _blocks.TryGetValue((long)param, out var b) ? b.Hash : null,
                "getblockheader" => _blocks.Values.Where(b => b.Hash == (string)param!)
                    .Select(b => new JObject { ["time"] = b.Time, ["mediantime"] = b.Time })
                    .FirstOrDefault(),
                _ => null,
            };
            var response = result is null
                ? new JObject { ["result"] = null, ["error"] = new JObject { ["code"] = -8, ["message"] = "not found" }, ["id"] = rpc["id"] }
                : new JObject { ["result"] = result, ["error"] = null, ["id"] = rpc["id"] };
            return Json(response.ToString());
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

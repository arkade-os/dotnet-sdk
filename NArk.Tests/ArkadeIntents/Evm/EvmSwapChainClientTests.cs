using System.Numerics;
using NArk.ArkadeIntents.Evm;

namespace NArk.Tests.ArkadeIntents.Evm;

public class EvmSwapChainClientTests
{
    private const long Now = 1_800_000_000;
    private static readonly byte[] Preimage = Enumerable.Repeat((byte)0xaa, 32).ToArray();
    private static readonly Erc20SwapValues Values = new(
        "e0e77a507412b120f6ede61f62295b1a7b2ff19d3dcc8f7253e51663470c888e",
        1_000_000,
        "0x1111111111111111111111111111111111111111",
        "0x2222222222222222222222222222222222222222",
        "0x3333333333333333333333333333333333333333",
        200);

    [Test]
    public async Task ProveLock_RequiresLatestAndHistoricalStatePlusBlockAge()
    {
        var rpc = new FakeRpc { BlockNumber = 100, BlockTimestamp = Now - 1 };
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(true));
        var client = Client(rpc, new FakeSender());

        var proof = await client.ProveLockAsync(Values);

        Assert.Multiple(() =>
        {
            Assert.That(proof.ObservedAtBlock, Is.EqualTo(new BigInteger(100)));
            Assert.That(proof.ProvenAtBlock, Is.EqualTo(new BigInteger(100)));
            Assert.That(rpc.Calls.Select(c => c.Block), Is.EqualTo(new BigInteger?[] { null, 100 }));
        });
    }

    [Test]
    public void ProveLock_RejectsAStateMissingAtTheDepthProbe()
    {
        var rpc = new FakeRpc { BlockNumber = 100, BlockTimestamp = Now - 10 };
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(false));

        Assert.That(async () => await Client(rpc, new FakeSender()).ProveLockAsync(Values),
            Throws.TypeOf<EvmSwapProofException>());
    }

    [Test]
    public async Task ProveLock_UsesTheConfiguredDepthGreaterThanOne()
    {
        var rpc = new FakeRpc { BlockNumber = 100, BlockTimestamp = Now - 10 };
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(true));

        var proof = await Client(rpc, new FakeSender(), minConfirmations: 3).ProveLockAsync(Values);

        Assert.That(proof.ProvenAtBlock, Is.EqualTo(new BigInteger(98)));
        Assert.That(rpc.Calls[1].Block, Is.EqualTo(new BigInteger(98)));
    }

    [Test]
    public async Task ClaimFor_VerifiesReceiptEventPreimageBalanceAndConsumedLock()
    {
        var rpc = new FakeRpc
        {
            BlockNumber = 100,
            BlockTimestamp = Now - 1,
            Receipt = new EvmTransactionReceipt(
                "0xtx", true, ClaimLogs()),
        };
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(false));
        var sender = new FakeSender();

        var result = await Client(rpc, sender).ClaimForAsync(Values, Preimage);

        Assert.Multiple(() =>
        {
            Assert.That(result.TransactionHash, Is.EqualTo("0xtx"));
            Assert.That(result.DeliveredAmount, Is.EqualTo(Values.Amount));
            Assert.That(sender.Request!.To, Is.EqualTo(Policy().SwapContractAddress));
            Assert.That(sender.Request.Data, Is.EqualTo(Erc20SwapCodec.ClaimForCall(Preimage, Values)));
            Assert.That(rpc.Calls[^2].Block, Is.Null, "lock state is re-read immediately before signing");
        });
    }

    [Test]
    public async Task ClaimFor_UsesTransactionLocalTransferEvidenceWithoutBalanceSnapshots()
    {
        var rpc = ClaimRpc();

        var result = await Client(rpc, new FakeSender()).ClaimForAsync(Values, Preimage);

        Assert.That(result.DeliveredAmount, Is.EqualTo(Values.Amount));
    }

    [Test]
    public void ClaimFor_RefusesWhenTheLockDisappearsImmediatelyBeforeSigning()
    {
        var rpc = new FakeRpc
        {
            BlockNumber = 100, BlockTimestamp = Now - 1,
        };
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(false));
        var sender = new FakeSender();

        Assert.That(async () => await Client(rpc, sender).ClaimForAsync(Values, Preimage),
            Throws.TypeOf<EvmSwapProofException>());
        Assert.That(sender.Request, Is.Null);
    }

    private static byte[] Bool(bool value)
    {
        var result = new byte[32];
        if (value) result[^1] = 1;
        return result;
    }

    private static EvmSwapChainClient Client(FakeRpc rpc, FakeSender sender, int minConfirmations = 1) =>
        new(rpc, sender, Policy(minConfirmations), new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(Now)));

    private static EvmSendPolicy Policy(int minConfirmations = 1) => new()
    {
        ChainId = 31_337,
        TokenAddress = Values.TokenAddress,
        SwapContractAddress = "0x00000000000000000000000000000000deadbeef",
        FastestSecondsPerBlock = 1,
        SlowestSecondsPerBlock = 1,
        MinConfirmations = minConfirmations,
        MinAgeSeconds = 1,
    };

    private static IReadOnlyList<EvmLog> ClaimLogs() =>
    [
        new EvmLog(Policy().SwapContractAddress,
            [Erc20SwapCodec.ClaimTopic, "0x" + Values.PaymentHash],
            "0x" + Convert.ToHexString(Preimage).ToLowerInvariant()),
        new EvmLog(Values.TokenAddress,
            [Erc20SwapCodec.TransferTopic, AddressTopic(Policy().SwapContractAddress), AddressTopic(Values.ClaimAddress)],
            "0x" + Values.Amount.ToString("x64")),
    ];

    private static string AddressTopic(string address) => "0x" + new string('0', 24) + address[2..];

    private static FakeRpc ClaimRpc()
    {
        var rpc = new FakeRpc
        {
            BlockNumber = 100, BlockTimestamp = Now - 1,
            Receipt = new EvmTransactionReceipt("0xtx", true, ClaimLogs()),
        };
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(true));
        rpc.SwapResults.Enqueue(Bool(false));
        return rpc;
    }

    private sealed class FakeRpc : IEvmSwapRpc
    {
        public BigInteger BlockNumber { get; init; }
        public long BlockTimestamp { get; init; }
        public Queue<byte[]> SwapResults { get; } = new();
        public EvmTransactionReceipt? Receipt { get; init; }
        public List<(string To, byte[] Data, BigInteger? Block)> Calls { get; } = [];

        public Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<BigInteger>(31_337);
        public Task<BigInteger> GetBlockNumberAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(BlockNumber);
        public Task<long> GetBlockTimestampAsync(BigInteger blockNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(BlockTimestamp);
        public Task<byte[]> CallAsync(string to, byte[] data, BigInteger? blockNumber = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((to, data, blockNumber));
            return Task.FromResult(SwapResults.Dequeue());
        }
        public Task<EvmTransactionReceipt> WaitForReceiptAsync(string transactionHash,
            CancellationToken cancellationToken = default) => Task.FromResult(Receipt!);
    }

    private sealed class FakeSender : IEvmTransactionSender
    {
        public EvmTransactionRequest? Request { get; private set; }
        public Task<string> SendAsync(EvmTransactionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult("0xtx");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

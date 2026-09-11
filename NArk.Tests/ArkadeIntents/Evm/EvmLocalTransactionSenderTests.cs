using System.Numerics;
using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Evm;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace NArk.Tests.ArkadeIntents.Evm;

public class EvmLocalTransactionSenderTests
{
    private const string PrivateKey = "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string Sender = "0xf39fd6e51aad88f6f4ce6ab8827279cfffb92266";
    private const string Contract = "0x00000000000000000000000000000000deadbeef";

    [Test]
    public async Task SignsAndBroadcastsAnEip1559TransactionWithNodeNonceFeesAndGas()
    {
        var handler = SenderHandler();
        var sender = Create(handler);

        var hash = await sender.SendAsync(new EvmTransactionRequest(31_337, Contract, [0xbc, 0x58]));

        var raw = handler.Requests.Single(r => r["method"]!.GetValue<string>() == "eth_sendRawTransaction")
            ["params"]![0]!.GetValue<string>();
        var transaction = (Transaction1559)TransactionFactory.CreateTransaction(raw);
        var recovered = EthECKeyBuilderFromSignedTransaction.GetEthECKey(transaction).GetPublicAddress();
        Assert.Multiple(() =>
        {
            Assert.That(hash, Is.EqualTo("0x" + new string('f', 64)));
            Assert.That(raw, Does.StartWith("0x02"));
            Assert.That(transaction.ChainId, Is.EqualTo(new BigInteger(31_337)));
            Assert.That(transaction.Nonce, Is.EqualTo(new BigInteger(5)));
            Assert.That(transaction.MaxPriorityFeePerGas, Is.EqualTo(new BigInteger(1_000_000_000)));
            Assert.That(transaction.MaxFeePerGas, Is.EqualTo(new BigInteger(5_000_000_000)));
            Assert.That(transaction.GasLimit, Is.EqualTo(new BigInteger(25_200)));
            Assert.That(recovered, Is.EqualTo(Sender).IgnoreCase);
        });
    }

    [Test]
    public async Task ConcurrentSendsSerializePendingNonceAllocation()
    {
        var nextNonce = 4;
        var handler = SenderHandler(() => Interlocked.Increment(ref nextNonce));
        var sender = Create(handler);

        await Task.WhenAll(
            sender.SendAsync(new EvmTransactionRequest(31_337, Contract, [1])),
            sender.SendAsync(new EvmTransactionRequest(31_337, Contract, [2])));

        var nonces = handler.Requests
            .Where(r => r["method"]!.GetValue<string>() == "eth_sendRawTransaction")
            .Select(r => (Transaction1559)TransactionFactory.CreateTransaction(
                r["params"]![0]!.GetValue<string>())).Select(t => t.Nonce).ToArray();
        Assert.That(nonces, Is.EqualTo(new BigInteger?[] { 5, 6 }));
    }

    [Test]
    public async Task DurableSendPersistsArtifactBeforeBroadcastAndResumeRebroadcastsExactBytes()
    {
        var events = new List<string>();
        var handler = SenderHandler(onSend: () => events.Add("broadcast"), returnComputedHash: true);
        IEvmDurableTransactionSender sender = Create(handler);
        EvmPreparedTransaction? artifact = null;
        var request = new EvmTransactionRequest(31_337, Contract, [0xbc, 0x58]);

        var hash = await sender.SendAsync(request,
            (prepared, _) =>
            {
                artifact = prepared;
                events.Add("persist:" + prepared.TransactionHash);
                return Task.CompletedTask;
            });

        Assert.Multiple(() =>
        {
            Assert.That(hash, Does.Match("^0x[0-9a-f]{64}$"));
            Assert.That(events, Is.EqualTo(new[] { "persist:" + hash, "broadcast" }));
            Assert.That(artifact!.ToString(), Does.Not.Contain(artifact.SignedTransaction));
        });

        var resumedHash = await sender.ResumeAsync(request, artifact!);
        var broadcasts = handler.Requests
            .Where(r => r["method"]!.GetValue<string>() == "eth_sendRawTransaction")
            .Select(r => r["params"]![0]!.GetValue<string>()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(resumedHash, Is.EqualTo(hash));
            Assert.That(broadcasts, Has.Length.EqualTo(2));
            Assert.That(broadcasts[1], Is.EqualTo(broadcasts[0]));
        });
    }

    [Test]
    public void DurableSendNeverBroadcastsWhenTheJournalCallbackFails()
    {
        var handler = SenderHandler(returnComputedHash: true);
        IEvmDurableTransactionSender sender = Create(handler);

        Assert.That(async () => await sender.SendAsync(
                new EvmTransactionRequest(31_337, Contract, [1]),
                (_, _) => throw new IOException("journal unavailable")),
            Throws.TypeOf<IOException>());
        Assert.That(handler.Requests.Any(r =>
            r["method"]!.GetValue<string>() == "eth_sendRawTransaction"), Is.False);
    }

    [Test]
    public async Task ResumeRejectsAnArtifactForAnotherCallBeforeBroadcast()
    {
        var handler = SenderHandler(returnComputedHash: true);
        IEvmDurableTransactionSender sender = Create(handler);
        EvmPreparedTransaction? artifact = null;
        await sender.SendAsync(new EvmTransactionRequest(31_337, Contract, [1]),
            (prepared, _) =>
            {
                artifact = prepared;
                return Task.CompletedTask;
            });

        Assert.That(async () => await sender.ResumeAsync(
                new EvmTransactionRequest(31_337, Contract, [2]), artifact!),
            Throws.TypeOf<EvmTransactionException>());
        Assert.That(handler.Requests.Count(r =>
            r["method"]!.GetValue<string>() == "eth_sendRawTransaction"), Is.EqualTo(1));
    }

    [Test]
    public async Task ResumeChecksTheConnectedChainBeforeSendingSecretCalldata()
    {
        var goodHandler = SenderHandler(returnComputedHash: true);
        IEvmDurableTransactionSender goodSender = Create(goodHandler);
        EvmPreparedTransaction? artifact = null;
        var request = new EvmTransactionRequest(31_337, Contract, [1]);
        await goodSender.SendAsync(request, (prepared, _) =>
        {
            artifact = prepared;
            return Task.CompletedTask;
        });
        var wrongChainHandler = SenderHandler(chainId: 1, returnComputedHash: true);
        IEvmDurableTransactionSender wrongChainSender = Create(wrongChainHandler);

        Assert.That(async () => await wrongChainSender.ResumeAsync(request, artifact!),
            Throws.TypeOf<EvmTransactionException>());
        Assert.That(wrongChainHandler.Requests.Any(r =>
            r["method"]!.GetValue<string>() == "eth_sendRawTransaction"), Is.False);
    }

    [Test]
    public async Task SeparateSenderInstancesSerializeTheSameGasPayerWithinTheProcess()
    {
        var submitted = 0;
        EvmJsonRpcClientTests.RpcHandler? handler = null;
        handler = SenderHandler(
            () => 5 + Volatile.Read(ref submitted),
            onSend: () => Interlocked.Increment(ref submitted),
            responseDelay: TimeSpan.FromMilliseconds(3));
        var first = Create(handler);
        var second = Create(handler);

        await Task.WhenAll(
            first.SendAsync(new EvmTransactionRequest(31_337, Contract, [1])),
            second.SendAsync(new EvmTransactionRequest(31_337, Contract, [2])));

        var nonces = handler.Requests
            .Where(r => r["method"]!.GetValue<string>() == "eth_sendRawTransaction")
            .Select(r => (Transaction1559)TransactionFactory.CreateTransaction(
                r["params"]![0]!.GetValue<string>())).Select(t => t.Nonce).ToArray();
        Assert.That(nonces, Is.EqualTo(new BigInteger?[] { 5, 6 }));
    }

    [Test]
    public void RefusesWrongConfiguredSenderWrongChainAndNodeRevert()
    {
        var handler = SenderHandler();
        Assert.That(() => new EvmLocalTransactionSender(
            EvmJsonRpcClientTests.Client(handler), Convert.FromHexString(PrivateKey),
            Options("0x1111111111111111111111111111111111111111")), Throws.ArgumentException);

        var sender = Create(handler);
        Assert.That(async () => await sender.SendAsync(new EvmTransactionRequest(1, Contract, [1])),
            Throws.TypeOf<EvmTransactionException>());

        var preimage = new string('c', 64);
        var reverting = SenderHandler(estimateError: true, errorMessage: "calldata=0xbc586b28" + preimage);
        var error = Assert.ThrowsAsync<EvmJsonRpcException>(async () => await Create(reverting).SendAsync(
            new EvmTransactionRequest(31_337, Contract, [1])));
        Assert.That(error!.ToString(), Does.Not.Contain(preimage));
        Assert.That(reverting.Requests.Any(r => r["method"]!.GetValue<string>() == "eth_sendRawTransaction"),
            Is.False);
    }

    [Test]
    public void RefusesNodeFeeAndGasEstimatesAboveConfiguredCaps()
    {
        var feeHandler = SenderHandler();
        var feeOptions = new EvmTransactionSenderOptions
        {
            ExpectedSenderAddress = Sender,
            MaxFeePerGasWei = 4_999_999_999,
            MaxPriorityFeePerGasWei = 2_000_000_000,
            MaxGasLimit = 500_000,
        };
        var feeSender = new EvmLocalTransactionSender(
            EvmJsonRpcClientTests.Client(feeHandler), Convert.FromHexString(PrivateKey), feeOptions);
        Assert.That(async () => await feeSender.SendAsync(
            new EvmTransactionRequest(31_337, Contract, [1])), Throws.TypeOf<EvmTransactionException>());

        var gasHandler = SenderHandler();
        var gasOptions = new EvmTransactionSenderOptions
        {
            ExpectedSenderAddress = Sender,
            MaxFeePerGasWei = 100_000_000_000,
            MaxPriorityFeePerGasWei = 10_000_000_000,
            MaxGasLimit = 25_199,
        };
        var gasSender = new EvmLocalTransactionSender(
            EvmJsonRpcClientTests.Client(gasHandler), Convert.FromHexString(PrivateKey), gasOptions);
        Assert.That(async () => await gasSender.SendAsync(
            new EvmTransactionRequest(31_337, Contract, [1])), Throws.TypeOf<EvmTransactionException>());

        Assert.That(feeHandler.Requests.Concat(gasHandler.Requests)
            .Any(r => r["method"]!.GetValue<string>() == "eth_sendRawTransaction"), Is.False);
    }

    [Test]
    public void KeyIsNotExposedByStringOrJsonSerialization()
    {
        var sender = Create(SenderHandler());

        Assert.That(sender.ToString(), Does.Not.Contain(PrivateKey).IgnoreCase);
        Assert.That(() => System.Text.Json.JsonSerializer.Serialize(sender), Throws.InvalidOperationException);
    }

    [Test]
    public void RefusesInvalidKeysAndInconsistentFeeCapsWithoutKeyDisclosure()
    {
        var rpc = EvmJsonRpcClientTests.Client(SenderHandler());
        var invalidKey = new byte[32];

        var keyError = Assert.Throws<ArgumentException>(() =>
            new EvmLocalTransactionSender(rpc, invalidKey, Options(Sender)));
        Assert.That(keyError!.ToString(), Does.Not.Contain(Convert.ToHexString(invalidKey)));

        var caps = new EvmTransactionSenderOptions
        {
            ExpectedSenderAddress = Sender,
            MaxFeePerGasWei = 1,
            MaxPriorityFeePerGasWei = 2,
            MaxGasLimit = 500_000,
        };
        Assert.That(() => new EvmLocalTransactionSender(
            rpc, Convert.FromHexString(PrivateKey), caps), Throws.ArgumentException);
    }

    private static EvmLocalTransactionSender Create(EvmJsonRpcClientTests.RpcHandler handler) => new(
        EvmJsonRpcClientTests.Client(handler), Convert.FromHexString(PrivateKey), Options(Sender));

    private static EvmTransactionSenderOptions Options(string sender) => new()
    {
        ExpectedSenderAddress = sender,
        BaseFeeMultiplier = 2,
        GasLimitBasisPoints = 12_000,
        MaxFeePerGasWei = 100_000_000_000,
        MaxPriorityFeePerGasWei = 10_000_000_000,
        MaxGasLimit = 500_000,
    };

    private static EvmJsonRpcClientTests.RpcHandler SenderHandler(
        Func<int>? nonce = null,
        bool estimateError = false,
        Action? onSend = null,
        TimeSpan? responseDelay = null,
        string errorMessage = "execution reverted",
        BigInteger? chainId = null,
        bool returnComputedHash = false) => new(request => request["method"]!.GetValue<string>() switch
        {
            "eth_chainId" => EvmJsonRpcClientTests.Result(request, "0x" + (chainId ?? 31_337).ToString("x")),
            "eth_getTransactionCount" => EvmJsonRpcClientTests.Result(request, "0x" + (nonce?.Invoke() ?? 5).ToString("x")),
            "eth_maxPriorityFeePerGas" => EvmJsonRpcClientTests.Result(request, "0x3b9aca00"),
            "eth_getBlockByNumber" => EvmJsonRpcClientTests.Result(request,
                new JsonObject { ["baseFeePerGas"] = "0x77359400" }),
            "eth_estimateGas" when estimateError => new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32_000, ["message"] = errorMessage },
            },
            "eth_estimateGas" => EvmJsonRpcClientTests.Result(request, "0x5208"),
            "eth_sendRawTransaction" => Sent(request, onSend, returnComputedHash),
            _ => throw new AssertionException("unexpected RPC method"),
        }, responseDelay);

    private static JsonObject Sent(JsonObject request, Action? onSend, bool returnComputedHash)
    {
        onSend?.Invoke();
        var result = returnComputedHash
            ? "0x" + new Sha3Keccack().CalculateHashFromHex(request["params"]![0]!.GetValue<string>())
            : "0x" + new string('f', 64);
        return EvmJsonRpcClientTests.Result(request, result);
    }
}

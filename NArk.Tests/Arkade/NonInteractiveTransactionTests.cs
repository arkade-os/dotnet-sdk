using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.Arkade.Scripts;
using NArk.Core;
using NArk.Core.Helpers;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Arkade;

public class NonInteractiveTransactionTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void AssetBearingLowLevelCoin_NeverReachesEmulator(bool refund, bool covenantAsset)
    {
        var contract = NonInteractiveTestData.Contract(asset: covenantAsset ? new VHTLCv2Asset(new byte[32], 0) : null);
        var funding = NonInteractiveTestData.Funding(contract, 50_000);
        var vtxo = NonInteractiveTestData.Vtxos(contract, funding).Single();
        if (!covenantAsset) vtxo = vtxo with { Assets = [new VtxoAsset(new string('a', 68), 1)] };
        using var handler = new RecordingEmulator();
        var spending = NonInteractiveTestData.Spending(Substitute.For<IWalletProvider>(), funding, handler);
        var script = refund ? contract.NonInteractiveRefund!.SenderPkScript : contract.NonInteractiveClaim!.ReceiverPkScript;
        var destination = ArkAddress.FromScriptPubKey(new Script(script), contract.Server!.ToXOnlyPubKey());
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var coin = refund ? contract.ToNonInteractiveRefundWithoutReceiverCoin("watch-only", vtxo)
                : contract.ToNonInteractiveClaimCoin("watch-only", vtxo, NonInteractiveTestData.Preimage);
            await spending.Spend("watch-only", [coin], [new ArkTxOut(ArkTxOutType.Vtxo, coin.Amount, destination)
            {
                Assets = covenantAsset ? null : [new ArkTxOutAsset(new string('a', 68), 1)]
            }]);
        });
        Assert.That(error!.Message, Does.Contain("BTC-only"));
        Assert.That(handler.ArkTx, Is.Null);
    }

    [TestCase("wrong-script")]
    [TestCase("under-value")]
    [TestCase("swapped")]
    [TestCase("subdust-conversion")]
    public void InvalidIndexedPayout_NeverReachesEmulator(string kind)
    {
        var contract = NonInteractiveTestData.Contract();
        var funding = kind == "subdust-conversion" ? NonInteractiveTestData.Funding(contract, 100, 99_900)
            : NonInteractiveTestData.Funding(contract, 30_000, 70_000);
        var coins = NonInteractiveTestData.Vtxos(contract, funding)
            .Select(v => contract.ToNonInteractiveClaimCoin("watch-only", v, NonInteractiveTestData.Preimage)).ToArray();
        using var handler = new RecordingEmulator();
        var wallet = Substitute.For<IWalletProvider>();
        wallet.GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((IArkadeWalletSigner?)null);
        var spending = NonInteractiveTestData.Spending(wallet, funding, handler,
            NonInteractiveTestData.ServerInfo() with { MaxOpReturnOutputs = 2 });
        var destination = ArkAddress.FromScriptPubKey(new Script(contract.NonInteractiveClaim!.ReceiverPkScript),
            contract.Server!.ToXOnlyPubKey());
        ArkTxOut[] outputs = [new(ArkTxOutType.Vtxo, coins[0].Amount, destination),
            new(ArkTxOutType.Vtxo, coins[1].Amount, destination)];
        if (kind == "wrong-script") outputs[0] = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(30_000), contract.GetArkAddress());
        if (kind == "under-value")
        {
            outputs[0].Value = Money.Satoshis(29_999);
            outputs[1].Value = Money.Satoshis(70_001);
        }
        if (kind == "swapped") Array.Reverse(outputs);
        Assert.ThrowsAsync<InvalidOperationException>(() => spending.Spend("watch-only", coins, outputs));
        Assert.That(handler.ArkTx, Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IndexedSpend_RejectsMixedInputsOrMissingPayouts(bool mixed)
    {
        var contract = NonInteractiveTestData.Contract();
        var vtxos = NonInteractiveTestData.Vtxos(contract, NonInteractiveTestData.Funding(contract, 30_000, 70_000));
        ArkCoin[] coins = [contract.ToNonInteractiveClaimCoin("watch-only", vtxos[0], NonInteractiveTestData.Preimage),
            mixed ? contract.ToClaimCoin("signer", vtxos[1], NonInteractiveTestData.Preimage)
                : contract.ToNonInteractiveClaimCoin("watch-only", vtxos[1], NonInteractiveTestData.Preimage)];
        var builder = new TransactionHelpers.ArkTransactionBuilder(Substitute.For<IClientTransport>(),
            Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(), Substitute.For<IIntentStorage>());
        TxOut[] outputs = mixed
            ? [new(Money.Satoshis(30_000), contract.GetScriptPubKey()), new(Money.Satoshis(70_000), contract.GetScriptPubKey())]
            : [new(Money.Satoshis(100_000), contract.GetScriptPubKey())];
        var error = Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ConstructArkTransaction(coins, outputs, NonInteractiveTestData.ServerInfo(), CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("indexed"));
    }

    [Test]
    public void IndexedSpend_RejectsDuplicateOutpointsWithDomainError()
    {
        var contract = NonInteractiveTestData.Contract();
        var vtxo = NonInteractiveTestData.Vtxos(
            contract, NonInteractiveTestData.Funding(contract, 100_000)).Single();
        var coin = contract.ToNonInteractiveClaimCoin(
            "watch-only", vtxo, NonInteractiveTestData.Preimage);
        var builder = new TransactionHelpers.ArkTransactionBuilder(Substitute.For<IClientTransport>(),
            Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(), Substitute.For<IIntentStorage>());
        TxOut[] outputs =
        [
            new(Money.Satoshis(100_000), new Script(contract.NonInteractiveClaim!.ReceiverPkScript)),
            new(Money.Satoshis(100_000), new Script(contract.NonInteractiveClaim.ReceiverPkScript)),
        ];

        var error = Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.ConstructArkTransaction(
                [coin, new ArkCoin(coin)], outputs, NonInteractiveTestData.ServerInfo(), CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("Duplicate lockup outpoints"));
    }

    [TestCase(30_000, 70_000)]
    [TestCase(70_000, 30_000)]
    public async Task SignerlessClaim_ReachesEmulatorWithAlignedUnmergedOutputs(long first, long second)
    {
        var contract = NonInteractiveTestData.Contract();
        var funding = NonInteractiveTestData.Funding(contract, first, second);
        var vtxos = NonInteractiveTestData.Vtxos(contract, funding);
        var coins = vtxos.Select(v => contract.ToNonInteractiveClaimCoin("watch-only", v, NonInteractiveTestData.Preimage)).ToArray();
        var wallet = Substitute.For<IWalletProvider>();
        wallet.GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((IArkadeWalletSigner?)null);
        using var handler = new RecordingEmulator();
        var spending = NonInteractiveTestData.Spending(wallet, funding, handler);
        var destination = ArkAddress.FromScriptPubKey(new Script(contract.NonInteractiveClaim!.ReceiverPkScript),
            contract.Server!.ToXOnlyPubKey());

        var txid = await spending.Spend("watch-only", coins,
            coins.Select(c => new ArkTxOut(ArkTxOutType.Vtxo, c.Amount, destination)).ToArray());

        var submitted = handler.ArkTx!;
        Assert.That(txid, Is.EqualTo(submitted.GetGlobalTransaction().GetHash()));
        Assert.That(handler.Checkpoints, Has.Count.EqualTo(2));
        for (var index = 0; index < 2; index++)
        {
            var checkpoint = handler.Checkpoints[index];
            Assert.That(checkpoint.Inputs.Single().PrevOut, Is.EqualTo(coins[index].Outpoint));
            Assert.That(submitted.Inputs[index].PrevOut.Hash, Is.EqualTo(checkpoint.GetGlobalTransaction().GetHash()));
            Assert.That(submitted.Outputs[index].ScriptPubKey, Is.EqualTo(destination.ScriptPubKey));
            Assert.That(submitted.Outputs[index].Value, Is.EqualTo(coins[index].Amount));
            foreach (var input in new[] { submitted.Inputs[index], checkpoint.Inputs.Single() })
            {
                Assert.That(input.GetArkFieldConditionWitness()!.Pushes.Single(), Is.EqualTo(NonInteractiveTestData.Preimage));
                Assert.That(input.GetTaprootScriptSpendSignatures(), Is.Empty);
            }
        }
        var packet = EmulatorPacket.FromTransaction(submitted.GetGlobalTransaction())!;
        Assert.That(packet.Entries.Select(e => e.Vin), Is.EqualTo(new ushort[] { 0, 1 }));
        Assert.That(packet.Entries.All(e => e.Script.SequenceEqual(contract.NonInteractiveClaimArkadeScript)), Is.True);
        await wallet.DidNotReceive().GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

}

internal static class NonInteractiveTestData
{
    internal static readonly byte[] Preimage = Enumerable.Repeat((byte)0x11, 32).ToArray();
    internal static byte[] KeyBytes(byte scalar) => new Key(Enumerable.Repeat(scalar, 32).ToArray()).PubKey.ToBytes()[1..];
    internal static OutputDescriptor Descriptor(byte scalar) =>
        KeyExtensions.ParseOutputDescriptor("02" + Convert.ToHexString(KeyBytes(scalar)).ToLowerInvariant(), Network.RegTest);

    internal static VHTLCv2Contract Contract(bool claim = true, bool refund = true, bool ninthLeaf = true,
        VHTLCv2Asset? asset = null) => new(
        Descriptor(2), Descriptor(3), Descriptor(4),
        new uint160(NBitcoin.Crypto.Hashes.RIPEMD160(NBitcoin.Crypto.Hashes.SHA256(Preimage), 32), false),
        new LockTime(1_800_000_000), new Sequence(144), new Sequence(288), new Sequence(432),
        claim ? new VHTLCv2NonInteractiveClaim([0x51, 0x20, .. KeyBytes(6)], ECXOnlyPubKey.Create(KeyBytes(5))) : null,
        refund ? new VHTLCv2NonInteractiveRefund([0x51, 0x20, .. KeyBytes(7)], ECXOnlyPubKey.Create(KeyBytes(5)), ninthLeaf) : null,
        asset);

    internal static Transaction Funding(VHTLCv2Contract contract, params long[] amounts)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        foreach (var amount in amounts) tx.Outputs.Add(new TxOut(Money.Satoshis(amount), contract.GetScriptPubKey()));
        return tx;
    }

    internal static ArkVtxo[] Vtxos(VHTLCv2Contract contract, Transaction funding) =>
        funding.Outputs.Select((o, i) => new ArkVtxo(contract.GetScriptPubKey().ToHex(), funding.GetHash().ToString(),
            (uint)i, (ulong)o.Value.Satoshi, null, null, false, DateTimeOffset.UtcNow, null, null)).ToArray();

    internal static ArkServerInfo ServerInfo() => new(Money.Satoshis(546), Descriptor(2), [], Network.RegTest,
        new Sequence(144), new Sequence(144),
        BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ECXOnlyPubKey.Create(KeyBytes(2)), new UnilateralPathArkTapScript(new Sequence(144), new NofNMultisigTapScript([ECXOnlyPubKey.Create(KeyBytes(2))])),
        new ArkOperatorFeeTerms("0", "0", "0", "0", "0"), "");

    internal static SpendingService Spending(IWalletProvider wallet, Transaction funding, RecordingEmulator handler,
        ArkServerInfo? serverInfo = null)
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(serverInfo ?? ServerInfo());
        var safety = Substitute.For<ISafetyService>();
        safety.TryLockByTimeAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(true);
        var prev = Substitute.For<IPrevArkTxProvider>();
        prev.ResolveAsync(Arg.Any<IReadOnlyCollection<uint256>>(), Arg.Any<Network>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<uint256, Transaction> { [funding.GetHash()] = funding });
        var emulator = new EmulatorClient(new HttpClient(handler), Options.Create(new EmulatorClientOptions { ServerUrl = "http://emulator" }));
        return new SpendingService(Substitute.For<IVtxoStorage>(), Substitute.For<IContractStorage>(),
            Substitute.For<ICoinService>(), wallet, Substitute.For<IContractService>(), transport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(), safety, Substitute.For<IIntentStorage>(), [],
            extensionPacketProviders: [new ArkadeEmulatorPacketProvider()],
            submitHandlers: [new ArkadeEmulatorSpendSubmitter(emulator, prev)]);
    }
}

internal sealed class RecordingEmulator : HttpMessageHandler
{
    internal PSBT? ArkTx { get; private set; }
    internal List<PSBT> Checkpoints { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/v1/tx"));
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var ark = body.RootElement.GetProperty("arkTx").GetString()!;
        var checkpoints = body.RootElement.GetProperty("checkpointTxs").EnumerateArray().Select(e => e.GetString()!).ToArray();
        ArkTx = PSBT.Parse(ark, Network.RegTest);
        Checkpoints.AddRange(checkpoints.Select(c => PSBT.Parse(c, Network.RegTest)));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { signedArkTx = ark, signedCheckpointTxs = checkpoints }),
                Encoding.UTF8, "application/json")
        };
    }
}

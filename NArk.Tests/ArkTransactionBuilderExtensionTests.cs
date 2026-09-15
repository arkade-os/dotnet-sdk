using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.ArkadeIntents.Assets;
using NArk.Core;
using NArk.Core.Assets;
using NArk.Core.Contracts;
using NArk.Core.Helpers;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

public class ArkTransactionBuilderExtensionTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task AssetInputOrdering_PreservesOfferAndUnknownExtensionPackets(bool reordered)
    {
        var serverKey = ECXOnlyPubKey.Create(new Key(Enumerable.Repeat((byte)2, 32).ToArray())
            .PubKey.TaprootInternalKey.ToBytes());
        var descriptor = serverKey.ToOutputDescriptor(Network.RegTest);
        var checkpointScript = new UnilateralPathArkTapScript(new Sequence(144), new NofNMultisigTapScript([serverKey]));
        var serverInfo = new ArkServerInfo(Money.Satoshis(330), descriptor, [], Network.RegTest,
            new Sequence(144), new Sequence(144),
            BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            serverKey, checkpointScript, new ArkOperatorFeeTerms("0", "0", "0", "0", "0"), "");
        // Grouping A/B/A by script forces a reorder regardless of the selector's random group order.
        var coins = new[] { MakeCoin(1, 30_000, 1), MakeCoin(2, 70_000, reordered ? 2 : 1), MakeCoin(3, 50_000, 1) };
        var assetId = AssetId.Create(new string('a', 64), 0);
        var asset = Packet.Create([AssetGroup.Create(assetId, null,
            [AssetInput.Create(0, 25), AssetInput.Create(1, 35), AssetInput.Create(2, 40)],
            [AssetOutput.Create(0, 100)], [])]);
        var offer = OfferPacket.FromOffer(new Offer
        {
            SwapPkScript = coins[0].TxOut.ScriptPubKey.ToBytes(),
            WantAmount = 24_750,
            OfferAsset = assetId,
            MakerPkScript = coins[1].TxOut.ScriptPubKey.ToBytes(),
            MakerPublicKey = serverKey.ToBytes(),
            EmulatorPubkey = serverKey.ToBytes(),
        });
        byte[] unknownPayload = [0xde, 0xad, 0xbe, 0xef];
        var extension = new Extension([offer, asset, new UnknownPacket(42, unknownPayload)]);
        var builder = new TransactionHelpers.ArkTransactionBuilder(Substitute.For<IClientTransport>(),
            Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(), Substitute.For<IIntentStorage>());

        var (psbt, checkpoints) = await builder.ConstructArkTransaction(coins,
            [new TxOut(Money.Satoshis(150_000), coins[0].TxOut.ScriptPubKey), extension.ToTxOut()],
            serverInfo, CancellationToken.None);

        var originalInputsInFinalOrder = checkpoints.Select(c => c.Psbt.Inputs.Single().PrevOut).ToArray();
        Assert.That(originalInputsInFinalOrder.SequenceEqual(coins.Select(coin => coin.Outpoint)), Is.EqualTo(!reordered),
            "The fixture must exercise the requested input-ordering branch.");
        var actual = Extension.FromTransaction(psbt.GetGlobalTransaction())!;
        Assert.Multiple(() =>
        {
            Assert.That(actual.GetAssetPacket()!.Groups.Single().Inputs.Select(input =>
                    (originalInputsInFinalOrder[input.Vin], input.Amount)),
                Is.EqualTo(new[] { (coins[0].Outpoint, 25UL), (coins[1].Outpoint, 35UL), (coins[2].Outpoint, 40UL) }));
            Assert.That(actual.Packets.Select(packet => packet.PacketType), Is.EqualTo(new byte[] { 3, 0, 42 }));
            Assert.That(actual.Packets.SingleOrDefault(packet => packet.PacketType == 3)?.SerializePacketData(),
                Is.EqualTo(offer.SerializePacketData()));
            Assert.That(actual.Packets.SingleOrDefault(packet => packet.PacketType == 42)?.SerializePacketData(),
                Is.EqualTo(unknownPayload));
        });

        ArkCoin MakeCoin(uint index, long amount, int scriptGroup)
        {
            var script = new GenericTapScript([Op.GetPushOp(scriptGroup), OpcodeType.OP_DROP, OpcodeType.OP_TRUE]);
            var contract = new GenericArkContract(descriptor, [script]);
            return new ArkCoin("watch-only", contract, DateTimeOffset.UnixEpoch, null, null,
                new OutPoint(uint256.One, index), new TxOut(Money.Satoshis(amount), contract.GetScriptPubKey()),
                null, script, null, null, null, false, false);
        }
    }
}

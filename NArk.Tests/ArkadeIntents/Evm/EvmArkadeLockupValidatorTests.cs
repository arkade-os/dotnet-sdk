using System.Numerics;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.Tests.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Evm;

public class EvmArkadeLockupValidatorTests
{
    private const string Token = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    private const string PaymentHash = "e0e77a507412b120f6ede61f62295b1a7b2ff19d3dcc8f7253e51663470c888e";

    [TestCase(true)]
    [TestCase(false)]
    public void ValidationSurfacesWhetherTheQuotedShapeHasTheEmulatorRefundPath(bool nineLeaf)
    {
        var setup = Setup(nineLeaf);
        var policy = Policy(requireEmulatorRefundPath: nineLeaf);

        var result = EvmArkadeLockupValidator.Validate(
            setup.Request, setup.Quote, policy, setup.ServerInfo, setup.Client, setup.RefundPkScript,
            "02" + Convert.ToHexString(setup.Emulator.ToBytes()).ToLowerInvariant());

        Assert.That(result.HasEmulatorRefundPath, Is.EqualTo(nineLeaf));
        Assert.That(result.Contract.NonInteractiveRefund!.WithoutReceiver, Is.EqualTo(nineLeaf));
    }

    [Test]
    public void WatchOnlyPolicyRefusesTheEightLeafShape()
    {
        var setup = Setup(nineLeaf: false);

        Assert.That(() => EvmArkadeLockupValidator.Validate(
            setup.Request, setup.Quote, Policy(true), setup.ServerInfo, setup.Client,
            setup.RefundPkScript, "02" + Convert.ToHexString(setup.Emulator.ToBytes()).ToLowerInvariant()),
            Throws.TypeOf<EvmSendQuoteException>());
    }

    private static SetupValues Setup(bool nineLeaf)
    {
        var serverInfo = TestServerInfo.WithSeconds(1_800);
        var client = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);
        var solver = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);
        var refundPkScript = LockupShapes.RandomP2trPkScript();
        var receiverPkScript = LockupShapes.RandomP2trPkScript();
        var emulator = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var delays = LightningCorridor.UnilateralDelays(serverInfo);
        var candidates = LightningCorridor.DeriveBothLockupShapes(
            serverInfo.SignerKey, client, solver,
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(PaymentHash)), false),
            new LockTime(1_800_014_400),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            new VHTLCv2NonInteractiveClaim(receiverPkScript, emulator), refundPkScript, emulator);
        var refundAddress = ArkAddress.FromScriptPubKey(
            new Script(refundPkScript), serverInfo.SignerKey.ToXOnlyPubKey()).ToString(false);
        var request = EvmSendProfile.Request(
            50_000, PaymentHash, "0x2222222222222222222222222222222222222222", refundAddress,
            Convert.ToHexString(client.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(), Token, new string('c', 64));
        var contract = nineLeaf ? candidates.NineLeaf : candidates.EightLeaf;
        var quote = new RfqQuote<EvmSendQuoteProfile>
        {
            SolverPubkey = Convert.ToHexString(solver.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
            RefundLocktime = 1_800_014_400,
            Profile = new EvmSendQuoteProfile
            {
                PaymentHash = PaymentHash,
                LockupAddress = contract.GetArkAddress().ToString(false),
                ReceiverPkScript = Convert.ToHexString(receiverPkScript).ToLowerInvariant(),
            },
        };
        return new SetupValues(request, quote, serverInfo, client, refundPkScript, emulator);
    }

    private static EvmSendPolicy Policy(bool requireEmulatorRefundPath) => new()
    {
        ChainId = 31_337,
        TokenAddress = Token,
        SwapContractAddress = "0x00000000000000000000000000000000deadbeef",
        FastestSecondsPerBlock = 1,
        SlowestSecondsPerBlock = 1,
        MinConfirmations = 1,
        MinAgeSeconds = 1,
        RequireEmulatorRefundPath = requireEmulatorRefundPath,
    };

    private sealed record SetupValues(
        EvmSendRfqRequest Request,
        RfqQuote<EvmSendQuoteProfile> Quote,
        NArk.Core.ArkServerInfo ServerInfo,
        OutputDescriptor Client,
        byte[] RefundPkScript,
        ECXOnlyPubKey Emulator);
}

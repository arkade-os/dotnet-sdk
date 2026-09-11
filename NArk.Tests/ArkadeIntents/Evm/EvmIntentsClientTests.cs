using System.Numerics;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Tests.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Evm;

[TestFixture]
public class EvmIntentsClientTests
{
    private const long Now = 1_800_000_000;
    private const string Token = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    private const string SwapContract = "0x00000000000000000000000000000000deadbeef";
    private const string ClaimAddress = "0x2222222222222222222222222222222222222222";
    private const string PreparedRfqId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Test]
    public async Task AVerifiedOutgoingQuoteIsImportedAndItsSecretIsPersistedBeforeReturn()
    {
        var ctx = Context();
        var secret = SwapLinkSecret.FromPreimage(Enumerable.Repeat((byte)0x42, 32).ToArray());

        var pending = await ctx.Client.CreateSendQuoteAsync(
            "wallet-1", 50_000, ClaimAddress, Policy(), ctx.Rfq, secret, PreparedRfqId);

        var saved = ctx.Intents.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IArkadeIntentStorage.SaveArkadeSwapIntent))
            .Select(c => (ArkadeSwapIntent)c.GetArguments()[0]!).Single();
        Assert.Multiple(() =>
        {
            Assert.That(ctx.Events, Is.EqualTo(new[] { "quote", "import", "save" }));
            Assert.That(pending.Secret.PaymentHash, Is.EqualTo(secret.PaymentHash));
            Assert.That(pending.RfqId, Is.EqualTo(PreparedRfqId));
            Assert.That(pending.Quote.FromAmount, Is.EqualTo(50_000));
            Assert.That(pending.LockupAddress, Is.EqualTo(pending.Contract.GetArkAddress().ToString(false)));
            Assert.That(saved.Type, Is.EqualTo(ArkadeSwapIntentType.BtcToEvm));
            Assert.That(saved.PaymentHash, Is.EqualTo(secret.PaymentHash));
            Assert.That(saved.EvmMetadata().Preimage,
                Is.EqualTo(Convert.ToHexString(secret.ExportPreimage()).ToLowerInvariant()));
            Assert.That(saved.EvmMetadata().Amount, Is.EqualTo("123456"));
        });
    }

    [Test]
    public void AWrongRpcChainPersistsAndImportsNothing()
    {
        var ctx = Context(chainId: 1);

        Assert.That(() => ctx.Client.CreateSendQuoteAsync(
                "wallet-1", 50_000, ClaimAddress, Policy(), ctx.Rfq),
            Throws.TypeOf<EvmSendQuoteException>());
        Assert.That(ctx.Events, Is.EqualTo(new[] { "quote" }));
    }

    private static Harness Context(long chainId = 31_337)
    {
        var serverInfo = TestServerInfo.WithSeconds(1_800);
        var refundDescriptor = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);
        var refundContract = new ArkPaymentContract(
            serverInfo.SignerKey, new Sequence(TimeSpan.FromSeconds(1_800)), refundDescriptor);
        var solver = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);
        var receiverPkScript = LockupShapes.RandomP2trPkScript();
        var emulator = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var emulatorHex = "02" + Convert.ToHexString(emulator.ToBytes()).ToLowerInvariant();
        var events = new List<string>();

        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(default).ReturnsForAnyArgs(serverInfo);
        var contracts = Substitute.For<IContractService>();
        contracts.DeriveContract(default!, default, default, default, default)
            .ReturnsForAnyArgs(refundContract);
        contracts.WhenForAnyArgs(c => c.ImportContract(default!, default!, default, default, default))
            .Do(_ => events.Add("import"));
        var intents = Substitute.For<IArkadeIntentStorage>();
        intents.WhenForAnyArgs(i => i.SaveArkadeSwapIntent(default!, default))
            .Do(_ => events.Add("save"));
        var evm = Substitute.For<IEvmSwapRpc>();
        evm.GetChainIdAsync(default).ReturnsForAnyArgs(new BigInteger(chainId));
        evm.GetBlockNumberAsync(default).ReturnsForAnyArgs(new BigInteger(100));
        var rfq = Substitute.For<IRfqTransport>();
        rfq.RequestEvmSendQuoteAsync(default!, default).ReturnsForAnyArgs(call =>
        {
            events.Add("quote");
            var request = call.Arg<EvmSendRfqRequest>();
            var delays = LightningCorridor.UnilateralDelays(serverInfo);
            var candidates = LightningCorridor.DeriveBothLockupShapes(
                serverInfo.SignerKey, refundDescriptor, solver,
                new uint160(SwapScriptValues.PreimageHashFromPaymentHash(
                    Convert.FromHexString(request.Profile.PaymentHash)), false),
                new LockTime(checked((uint)(Now + 14_400))),
                new Sequence(TimeSpan.FromSeconds(delays.Claim)),
                new Sequence(TimeSpan.FromSeconds(delays.Refund)),
                new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
                new VHTLCv2NonInteractiveClaim(receiverPkScript, emulator),
                refundContract.GetArkAddress().ScriptPubKey.ToBytes(), emulator);
            return Quote(request, solver, receiverPkScript, candidates.NineLeaf);
        });
        var client = new EvmIntentsClient(
            transport, contracts, intents, evm,
            Options.Create(new ArkadeIntentsOptions { EmulatorPubkeyOverride = emulatorHex }),
            new TestClock(Now));
        return new Harness(client, contracts, intents, rfq, events);
    }

    private static RfqQuote<EvmSendQuoteProfile> Quote(
        EvmSendRfqRequest request,
        OutputDescriptor solver,
        byte[] receiverPkScript,
        VHTLCv2Contract contract) => new()
    {
        V = 1,
        Type = "rfq_quote",
        RfqId = request.RfqId,
        Pair = request.Pair,
        FromAmount = request.Amount,
        ToAtomicAmount = new BigInteger(123_456),
        SolverPubkey = Convert.ToHexString(solver.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
        ValidUntil = Now + 600,
        RefundLocktime = Now + 14_400,
        Profile = new EvmSendQuoteProfile
        {
            PaymentHash = request.Profile.PaymentHash,
            LockupAddress = contract.GetArkAddress().ToString(false),
            ReceiverPkScript = Convert.ToHexString(receiverPkScript).ToLowerInvariant(),
            EvmTimeoutBlock = 3_700,
            EvmRefundAddress = "0x3333333333333333333333333333333333333333",
            EvmContractAddress = SwapContract,
            EvmChainId = 31_337,
            MinConfirmations = 1,
            MinAgeSeconds = 0,
        },
    };

    private static EvmSendPolicy Policy() => new()
    {
        ChainId = 31_337,
        TokenAddress = Token,
        SwapContractAddress = SwapContract,
        FastestSecondsPerBlock = 1,
        SlowestSecondsPerBlock = 1,
        MinConfirmations = 1,
        MinAgeSeconds = 0,
    };

    private sealed record Harness(
        EvmIntentsClient Client,
        IContractService Contracts,
        IArkadeIntentStorage Intents,
        IRfqTransport Rfq,
        List<string> Events);

    private sealed class TestClock(long now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(now);
    }
}

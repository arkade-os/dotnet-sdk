using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Tests.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>Independent onchain ingress reuses the route secret while paying the outgoing covenant.</summary>
[TestFixture]
public class OnchainLinkedReceiveTests
{
    private const long Now = 1_800_000_000;
    private const string PreparedRfqId = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    /// <summary>The wire request must pin H, exact output, L, and the merchant's receiver/refund key.</summary>
    [Test]
    public async Task LinkedRequestUsesCallerSecretExactToOutgoingPayoutAndReservedRfqId()
    {
        var ctx = Context();
        var pending = await Receive(ctx);
        var request = ctx.Requests.Single();

        Assert.Multiple(() =>
        {
            Assert.That(request.RfqId, Is.EqualTo(PreparedRfqId));
            Assert.That(request.AmountSide, Is.EqualTo(RfqAmountSide.To));
            Assert.That(request.Amount, Is.EqualTo(50_000));
            Assert.That(request.Profile.PaymentHash, Is.EqualTo(ctx.Secret.PaymentHash));
            Assert.That(request.Profile.PayoutAddress, Is.EqualTo(ctx.Outgoing.ToString(false)));
            Assert.That(request.Profile.PayoutPubkey, Is.EqualTo(KeyHex(4)));
            Assert.That(request.Profile.RefundPubkey, Is.EqualTo(KeyHex(4)));
            Assert.That(request.Profile.PayoutPubkey, Is.Not.EqualTo(KeyHex(7)));
            Assert.That(request.Profile.ClaimPacket, Is.Not.Null.And.Not.Empty);
            Assert.That(pending.RfqId, Is.EqualTo(PreparedRfqId));
            Assert.That(pending.PaymentHash, Is.EqualTo(ctx.Secret.PaymentHash));
            Assert.That(pending.Preimage, Is.EqualTo(Enumerable.Repeat((byte)0x42, 32).ToArray()));
            Assert.That(pending.PayoutAddress, Is.EqualTo(ctx.Outgoing.ToString(false)));
            Assert.That(pending.FundAmountSats, Is.EqualTo(50_150));
        });
    }

    /// <summary>M must be imported with its NI payout pinned to L, and P retained only by SDK storage.</summary>
    [Test]
    public async Task LinkedCovenantAndSharedPreimageAreDurableBeforeReturningTheL1Address()
    {
        var ctx = Context();
        var pending = await Receive(ctx);
        var saved = ctx.Saved.Single();
        var imported = ctx.Imported.Single();

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Events, Is.EqualTo(new[] { "quote", "import", "save" }));
            Assert.That(imported.GetScriptPubKey(), Is.EqualTo(pending.Contract.GetScriptPubKey()));
            Assert.That(imported.NonInteractiveClaim!.ReceiverPkScript, Is.EqualTo(ctx.Outgoing.ScriptPubKey.ToBytes()));
            Assert.That(imported.ReceiverKey.ToBytes(), Is.EqualTo(Convert.FromHexString(KeyHex(4))));
            Assert.That(saved.Id, Is.EqualTo(PreparedRfqId));
            Assert.That(saved.WalletId, Is.EqualTo("wallet-1"));
            Assert.That(saved.Type, Is.EqualTo(ArkadeSwapIntentType.OnchainToBtc));
            Assert.That(saved.PaymentHash, Is.EqualTo(ctx.Secret.PaymentHash));
            Assert.That(saved.OnchainMetadata().Preimage, Is.EqualTo("4242424242424242424242424242424242424242424242424242424242424242"));
            Assert.That(saved.WantAmount, Is.EqualTo(Money.Satoshis(50_000)));
            Assert.That(saved.OfferAmount, Is.EqualTo(Money.Satoshis(50_150)));
            Assert.That(saved.SwapAddress, Is.EqualTo(pending.LockupAddress));
            Assert.That(saved.OnchainMetadata().PayoutAddress, Is.EqualTo(RefundAddress.ToString()));
        });
    }

    /// <summary>Linked receive must not consume another HD index or derive a different secret.</summary>
    [Test]
    public async Task LinkedReceiveDoesNotDeriveAnyFreshContractOrRequestAWalletSigner()
    {
        var ctx = Context();
        await Receive(ctx);
        Assert.That(ctx.Contracts.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IContractService.DeriveContract)), Is.False);
        Assert.That(ctx.Wallets.ReceivedCalls(), Is.Empty);
    }

    /// <summary>An omitted optional identity still produces a valid new independent RFQ.</summary>
    [Test]
    public async Task LinkedReceiveCanGenerateAnRfqIdWhenTheCallerDidNotReserveOne()
    {
        var ctx = Context();
        var pending = await Receive(ctx, rfqId: null);
        Assert.That(pending.RfqId, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(pending.RfqId, Is.EqualTo(ctx.Requests.Single().RfqId));
        Assert.That(pending.PaymentHash, Is.EqualTo(ctx.Secret.PaymentHash));
    }

    /// <summary>Malformed prepared identities must not produce a remote negotiation.</summary>
    [TestCase("")]
    [TestCase("secret-invalid-id")]
    [TestCase("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void InvalidPreparedRfqIdIsRejectedBeforeAnyQuote(string rfqId)
    {
        var ctx = Context();
        var error = Assert.ThrowsAsync<ArgumentException>(() => Receive(ctx, rfqId));
        Assert.That(error!.ToString(), Does.Not.Contain("secret-invalid-id"));
        Assert.That(ctx.Events, Is.Empty);
    }

    /// <summary>Invalid exact-output amounts must not be sent to a solver.</summary>
    [TestCase(0)]
    [TestCase(-1)]
    public void NonpositiveOutputIsRejectedBeforeAnyQuote(long amount)
    {
        var ctx = Context();
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Receive(ctx, amount: amount));
        Assert.That(ctx.Events, Is.Empty);
    }

    /// <summary>A solver cannot silently resize the exact amount required by outgoing L.</summary>
    [TestCase(-1)]
    [TestCase(1)]
    public void ResizedOutputIsRejectedWithoutImportOrPersistence(long delta)
    {
        var ctx = Context(delta);
        Assert.ThrowsAsync<OnchainReceiveNotFundableException>(() => Receive(ctx));
        Assert.That(ctx.Events, Is.EqualTo(new[] { "quote" }));
    }

    private static Task<PendingOnchainReceive> Receive(Harness ctx, string? rfqId = PreparedRfqId, long amount = 50_000) =>
        ctx.Client.ReceiveFromOnchainIntoAsync("wallet-1", amount, ctx.Rfq, KeyFor(11).PubKey.Compress().ToHex(),
            RefundAddress, ctx.Secret, ctx.Outgoing, ctx.Receiver, rfqId: rfqId);

    private static Harness Context(long quoteDelta = 0)
    {
        var server = TestServerInfo.WithSeconds(4096);
        var receiver = new ArkPaymentContract(server.SignerKey, new Sequence(TimeSpan.FromSeconds(4096)), Descriptor(4));
        var outgoing = new ArkPaymentContract(server.SignerKey, new Sequence(TimeSpan.FromSeconds(4096)), Descriptor(7)).GetArkAddress();
        var emulator = ECXOnlyPubKey.Create(KeyFor(11).PubKey.TaprootInternalKey.ToBytes());
        var events = new List<string>();
        var requests = new List<RfqRequest<OnchainReceiveRequestProfile>>();
        var imported = new List<VHTLCv2Contract>();
        var saved = new List<ArkadeSwapIntent>();
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(default).ReturnsForAnyArgs(server);
        var contracts = Substitute.For<IContractService>();
        contracts.DeriveContract(default!, default, default, default, default).ReturnsForAnyArgs(receiver);
        contracts.WhenForAnyArgs(c => c.ImportContract(default!, default!, default, default, default)).Do(call =>
        {
            events.Add("import");
            imported.Add((VHTLCv2Contract)call.ArgAt<ArkContract>(1));
        });
        var intents = Substitute.For<IArkadeIntentStorage>();
        intents.WhenForAnyArgs(i => i.SaveArkadeSwapIntent(default!, default)).Do(call =>
        {
            events.Add("save");
            saved.Add(call.Arg<ArkadeSwapIntent>());
        });
        var rfq = Substitute.For<IRfqTransport>();
        rfq.RequestQuoteAsync<OnchainReceiveRequestProfile, OnchainReceiveQuoteProfile>(default!, default)
            .ReturnsForAnyArgs(call =>
            {
                events.Add("quote");
                var request = call.Arg<RfqRequest<OnchainReceiveRequestProfile>>();
                requests.Add(request);
                var profile = request.Profile;
                var hash = Convert.FromHexString(profile.PaymentHash);
                var refundKey = ECXOnlyPubKey.Create(Convert.FromHexString(profile.RefundPubkey));
                var htlc = OnchainHtlc.Derive(new uint256(hash, false), Descriptor(6).ToXOnlyPubKey(), refundKey,
                    Now + 43_200, Network.RegTest);
                var delays = LightningCorridor.UnilateralDelays(server);
                var refundScript = new ArkPaymentContract(server.SignerKey, new Sequence(TimeSpan.FromSeconds(4096)), Descriptor(5)).GetScriptPubKey().ToBytes();
                var candidates = LightningCorridor.DeriveBothLockupShapes(server.SignerKey, Descriptor(5),
                    LightningCorridor.DescriptorForXOnly(profile.PayoutPubkey, Network.RegTest),
                    new uint160(SwapScriptValues.PreimageHashFromPaymentHash(hash), false), new LockTime((uint)(Now + 21_600)),
                    new Sequence(TimeSpan.FromSeconds(delays.Claim)), new Sequence(TimeSpan.FromSeconds(delays.Refund)),
                    new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
                    new VHTLCv2NonInteractiveClaim(ArkAddress.Parse(profile.PayoutAddress).ScriptPubKey.ToBytes(), emulator), refundScript, emulator);
                return new RfqQuote<OnchainReceiveQuoteProfile>
                {
                    V = 1, Type = "rfq_quote", RfqId = request.RfqId, Pair = request.Pair,
                    FromAmount = 50_150, ToAmount = 50_000 + quoteDelta, SolverPubkey = KeyHex(5),
                    ValidUntil = Now + 600, RefundLocktime = Now + 21_600,
                    Profile = new OnchainReceiveQuoteProfile
                    {
                        HtlcAddress = htlc.Address.ToString(), HtlcLocktime = Now + 43_200, MinConfirmations = 1,
                        ClaimPubkey = KeyHex(6), LockupAddress = candidates.NineLeaf.GetArkAddress().ToString(false),
                        SolverRefundPkScript = Convert.ToHexString(refundScript).ToLowerInvariant()
                    }
                };
            });
        var wallets = Substitute.For<IWalletProvider>();
        var client = new OnchainIntentsClient(transport, contracts, Substitute.For<ISpendingService>(), intents,
            Substitute.For<IContractStorage>(), Substitute.For<IVtxoStorage>(), wallets, Substitute.For<IBitcoinBlockchain>(),
            options: Options.Create(new ArkadeIntentsOptions { EmulatorPubkeyOverride = KeyFor(11).PubKey.Compress().ToHex() }),
            time: new TestClock());
        return new Harness(client, rfq, contracts, wallets, receiver, outgoing,
            SwapLinkSecret.FromPreimage(Enumerable.Repeat((byte)0x42, 32).ToArray()), events, requests, imported, saved);
    }

    private static BitcoinAddress RefundAddress => BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest);
    private static Key KeyFor(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());
    private static OutputDescriptor Descriptor(byte seed) => KeyExtensions.ParseOutputDescriptor(KeyFor(seed).PubKey.ToHex(), Network.RegTest);
    private static string KeyHex(byte seed) => Convert.ToHexString(KeyFor(seed).PubKey.TaprootInternalKey.ToBytes()).ToLowerInvariant();
    private sealed record Harness(OnchainIntentsClient Client, IRfqTransport Rfq, IContractService Contracts,
        IWalletProvider Wallets, ArkContract Receiver, ArkAddress Outgoing, SwapLinkSecret Secret, List<string> Events,
        List<RfqRequest<OnchainReceiveRequestProfile>> Requests, List<VHTLCv2Contract> Imported, List<ArkadeSwapIntent> Saved);
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }
}

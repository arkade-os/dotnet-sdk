using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.ArkadeIntents.SolverRegistry;
using NArk.Core.Contracts;
using NArk.Tests.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Composition;

/// <summary>The composer must link independently verified quote results without coordinating solvers.</summary>
[TestFixture]
public class ComposedSwapClientTests
{
    private const long Now = 1_800_000_000;
    private const string OutgoingId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IngressId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ClaimAddress = "0x2222222222222222222222222222222222222222";
    private const string Token = "0x1111111111111111111111111111111111111111";
    private const string RefundAddress = "0x3333333333333333333333333333333333333333";
    private const string SwapContract = "0x4444444444444444444444444444444444444444";

    /// <summary>No ingress negotiation may start until the outgoing quote has finished.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task OutgoingQuoteCompletesBeforeIngressStarts(bool onchain)
    {
        var ctx = new Harness { HoldOutgoing = true };
        var creating = Create(ctx, onchain);
        await ctx.OutgoingEntered.Task;
        Assert.That(ctx.IngressCalls, Is.Empty);
        Assert.That(creating.IsCompleted, Is.False);

        ctx.OutgoingGate.SetResult(ctx.PendingOutgoing!);
        await creating;

        Assert.That(ctx.Events, Is.EqualTo(new[] { "outgoing", onchain ? "onchain" : "lightning" }));
    }

    /// <summary>Both rails must receive the same route secret, exact base amount, L and wallet-owned receiver.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task LinkedArgumentsAndReturnedFeeExpiryMatchTheIndependentQuotes(bool onchain)
    {
        var ctx = new Harness();
        var result = await Create(ctx, onchain);
        var outgoing = ctx.OutgoingCalls.Single();
        var ingress = ctx.IngressCalls.Single();
        Assert.Multiple(() =>
        {
            Assert.That(outgoing.WalletId, Is.EqualTo("wallet-1"));
            Assert.That(outgoing.Amount, Is.EqualTo(50_000));
            Assert.That(outgoing.ClaimAddress, Is.EqualTo(ClaimAddress));
            Assert.That(outgoing.Policy, Is.SameAs(ctx.Policy));
            Assert.That(outgoing.Transport, Is.SameAs(ctx.OutgoingTransport));
            Assert.That(outgoing.Card, Is.SameAs(ctx.OutgoingCard));
            Assert.That(outgoing.RfqId, Is.EqualTo(OutgoingId));
            Assert.That(ingress.RfqId, Is.EqualTo(IngressId));
            Assert.That(ingress.RfqId, Is.Not.EqualTo(outgoing.RfqId));
            Assert.That(ingress.WalletId, Is.EqualTo("wallet-1"));
            Assert.That(ingress.Amount, Is.EqualTo(50_000));
            Assert.That(ingress.Transport, Is.SameAs(ctx.IngressTransport));
            Assert.That(ingress.Card, Is.SameAs(ctx.IngressCard));
            Assert.That(ingress.Secret, Is.SameAs(outgoing.Secret));
            Assert.That(ingress.Secret.PaymentHash, Is.EqualTo(ctx.PendingOutgoing!.Secret.PaymentHash));
            Assert.That(ingress.Payout.ToString(false), Is.EqualTo(ctx.PendingOutgoing.LockupAddress));
            Assert.That(ingress.Receiver, Is.SameAs(ctx.PendingOutgoing.RefundContract));
            Assert.That(ingress.CovclaimdKey, Is.EqualTo("configured-emulator-key"));
            Assert.That(result.Fee, Is.EqualTo(150));
            Assert.That(result.Expiry, Is.EqualTo(Now + 365));
            Assert.That(result.FromAmount, Is.EqualTo(50_150));
            Assert.That(result.ToAmount, Is.EqualTo(50_000));
            Assert.That(result.PaymentHash, Is.EqualTo(outgoing.Secret.PaymentHash));
            Assert.That(result.SolverPubkey, Is.EqualTo(ctx.PendingOutgoing.Quote.SolverPubkey));
            Assert.That(ingress.L1RefundAddress?.ToString(), Is.EqualTo(onchain ? ctx.L1Refund.ToString() : null));
        });
    }

    /// <summary>Direct Arkade exposes only L and never invokes an ingress quote client.</summary>
    [Test]
    public async Task DirectArkadeCreatesOnlyTheOutgoingQuote()
    {
        var ctx = new Harness();
        var route = await ctx.Client.CreateArkadeAsync("wallet-1", 50_000, ClaimAddress,
            ctx.Policy, ctx.OutgoingTransport, OutgoingId, ctx.OutgoingCard);
        Assert.Multiple(() =>
        {
            Assert.That(ctx.Events, Is.EqualTo(new[] { "outgoing" }));
            Assert.That(ctx.IngressCalls, Is.Empty);
            Assert.That(route.Outgoing, Is.SameAs(ctx.PendingOutgoing));
            Assert.That(route.Outgoing.RfqId, Is.EqualTo(OutgoingId));
            Assert.That(route.Outgoing.Quote.FromAmount, Is.EqualTo(50_000));
            Assert.That(route.CheckoutExpiresAt, Is.EqualTo(Now + 565));
        });
    }

    /// <summary>Alternative prompts must not share a secret.</summary>
    [Test]
    public async Task IndependentRailInvocationsReceiveIndependentSecrets()
    {
        var ctx = new Harness();
        var first = await ctx.Client.CreateLightningAsync("wallet-1", 50_000, ClaimAddress, ctx.Policy,
            ctx.OutgoingTransport, ctx.IngressTransport, "configured-emulator-key");
        var second = await ctx.Client.CreateOnchainAsync("wallet-1", 50_000, ClaimAddress, ctx.Policy,
            ctx.OutgoingTransport, ctx.IngressTransport, "configured-emulator-key", ctx.L1Refund);
        Assert.That(first.Outgoing.Secret.PaymentHash, Is.Not.EqualTo(second.Outgoing.Secret.PaymentHash));
        Assert.That(first.Outgoing.RfqId, Is.Not.EqualTo(second.Outgoing.RfqId));
        Assert.That(first.Ingress.RfqId, Is.Not.EqualTo(second.Ingress.RfqId));
    }

    /// <summary>Prepared identity reuse must be rejected before any quote.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public void SamePreparedIdsNeverReachAQuoteClient(bool onchain)
    {
        var ctx = new Harness();
        Assert.ThrowsAsync<ArgumentException>(() => Create(ctx, onchain, IngressId, IngressId));
        Assert.That(ctx.Events, Is.Empty);
    }

    /// <summary>A returned route must preserve H, exact amount, L, distinct identities and nonnegative spread.</summary>
    [TestCase(false, "hash")]
    [TestCase(true, "hash")]
    [TestCase(false, "to")]
    [TestCase(true, "to")]
    [TestCase(false, "payout")]
    [TestCase(true, "payout")]
    [TestCase(false, "same-returned-id")]
    [TestCase(true, "same-returned-id")]
    [TestCase(false, "negative-spread")]
    [TestCase(true, "negative-spread")]
    [TestCase(false, "outgoing-amount")]
    [TestCase(true, "outgoing-amount")]
    public void MismatchedQuoteFactsDoNotProduceARoute(bool onchain, string fault)
    {
        var ctx = new Harness { Fault = fault };
        Assert.ThrowsAsync<InvalidOperationException>(() => Create(ctx, onchain));
    }

    /// <summary>The earlier deadline limits checkout; insufficient room must reject the route.</summary>
    [TestCase(false, 600, 400, 365)]
    [TestCase(true, 600, 400, 365)]
    [TestCase(false, 300, 400, 265)]
    [TestCase(true, 300, 400, 265)]
    [TestCase(false, 95, 400, 60)]
    [TestCase(true, 95, 400, 60)]
    public async Task ExpiryReservesTheConfiguredOutgoingSafetyMargin(bool onchain, long outgoingWindow, long ingressWindow, long expiry)
    {
        var ctx = new Harness { OutgoingWindow = outgoingWindow, IngressWindow = ingressWindow };
        var result = await Create(ctx, onchain);
        Assert.That(result.Expiry, Is.EqualTo(Now + expiry));
    }

    /// <summary>A quote too close to expiry cannot be exposed for checkout.</summary>
    [TestCase(false, 94, 400)]
    [TestCase(true, 94, 400)]
    [TestCase(false, 600, 94)]
    [TestCase(true, 600, 94)]
    public void InsufficientCheckoutWindowRejectsTheRoute(bool onchain, long outgoingWindow, long ingressWindow)
    {
        var ctx = new Harness { OutgoingWindow = outgoingWindow, IngressWindow = ingressWindow };
        Assert.ThrowsAsync<InvalidOperationException>(() => Create(ctx, onchain));
    }

    private static async Task<RouteResult> Create(Harness ctx, bool onchain, string? outgoingId = OutgoingId, string? ingressId = IngressId)
    {
        if (onchain)
        {
            var route = await ctx.Client.CreateOnchainAsync("wallet-1", 50_000, ClaimAddress, ctx.Policy,
                ctx.OutgoingTransport, ctx.IngressTransport, "configured-emulator-key", ctx.L1Refund,
                outgoingId, ingressId, ctx.OutgoingCard, ctx.IngressCard);
            return new(route.CheckoutExpiresAt, route.IngressFeeSats, route.Ingress.Quote.FromAmount,
                route.Ingress.Quote.ToAmount, route.Ingress.PaymentHash, route.Ingress.Quote.SolverPubkey);
        }
        var lightning = await ctx.Client.CreateLightningAsync("wallet-1", 50_000, ClaimAddress, ctx.Policy,
            ctx.OutgoingTransport, ctx.IngressTransport, "configured-emulator-key", outgoingId, ingressId,
            ctx.OutgoingCard, ctx.IngressCard);
        return new(lightning.CheckoutExpiresAt, lightning.IngressFeeSats, lightning.Ingress.Quote.FromAmount,
            lightning.Ingress.Quote.ToAmount, lightning.Ingress.PaymentHash, lightning.Ingress.Quote.SolverPubkey);
    }

    private sealed record RouteResult(long Expiry, long Fee, long FromAmount, long ToAmount, string PaymentHash, string SolverPubkey);
    private sealed record OutgoingCall(string WalletId, long Amount, string ClaimAddress, EvmSendPolicy Policy,
        IRfqTransport Transport, SwapLinkSecret Secret, string? RfqId, SolverCard? Card);
    private sealed record IngressCall(string WalletId, long Amount, IRfqTransport Transport, string CovclaimdKey,
        SwapLinkSecret Secret, ArkAddress Payout, ArkContract Receiver, string? RfqId, SolverCard? Card, BitcoinAddress? L1RefundAddress);

    private sealed class Harness
    {
        internal string? Fault { get; init; }
        internal bool HoldOutgoing { get; init; }
        internal long OutgoingWindow { get; init; } = 600;
        internal long IngressWindow { get; init; } = 400;
        internal ComposedSwapClient Client { get; }
        internal IRfqTransport OutgoingTransport { get; } = Substitute.For<IRfqTransport>();
        internal IRfqTransport IngressTransport { get; } = Substitute.For<IRfqTransport>();
        internal SolverCard OutgoingCard { get; } = new() { Name = "outgoing" };
        internal SolverCard IngressCard { get; } = new() { Name = "ingress" };
        internal List<string> Events { get; } = [];
        internal List<OutgoingCall> OutgoingCalls { get; } = [];
        internal List<IngressCall> IngressCalls { get; } = [];
        internal TaskCompletionSource OutgoingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<PendingEvmSend> OutgoingGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PendingEvmSend? PendingOutgoing { get; private set; }
        internal BitcoinAddress L1Refund { get; } = BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest);
        internal EvmSendPolicy Policy { get; } = new()
        {
            ChainId = 31_337,
            TokenAddress = Token,
            SwapContractAddress = SwapContract,
            FastestSecondsPerBlock = 1,
            SlowestSecondsPerBlock = 1,
            MinConfirmations = 1,
            MinAgeSeconds = 0
        };
        private readonly NArk.Core.ArkServerInfo _server = TestServerInfo.WithSeconds(4096);
        private readonly OutputDescriptor _merchant = Descriptor(4);
        private readonly OutputDescriptor _solver = Descriptor(5);
        private readonly ECXOnlyPubKey _emulator = Descriptor(11).ToXOnlyPubKey();

        internal Harness()
        {
            var evm = Substitute.For<IEvmOutgoingQuoteClient>();
            evm.CreateSendQuoteAsync(default!, default, default!, default!, default!, default, default, default, default, default)
                .ReturnsForAnyArgs(call =>
                {
                    Events.Add("outgoing");
                    var secret = call.ArgAt<SwapLinkSecret>(5);
                    OutgoingCalls.Add(new(call.ArgAt<string>(0), call.ArgAt<long>(1), call.ArgAt<string>(2),
                        call.ArgAt<EvmSendPolicy>(3), call.ArgAt<IRfqTransport>(4), secret,
                        call.ArgAt<string?>(6), call.ArgAt<SolverCard?>(7)));
                    PendingOutgoing = Outgoing(secret, call.ArgAt<string?>(6) ?? RfqProtocol.NewRfqId());
                    OutgoingEntered.TrySetResult();
                    return HoldOutgoing ? OutgoingGate.Task : Task.FromResult(PendingOutgoing);
                });
            var lightning = Substitute.For<ILightningIngressQuoteClient>();
            lightning.ReceiveFromLightningIntoAsync(default!, default, default!, default!, default!, default!, default!, default, default, default)
                .ReturnsForAnyArgs(call =>
                {
                    Events.Add("lightning");
                    var input = new IngressCall(call.ArgAt<string>(0), call.ArgAt<long>(1), call.ArgAt<IRfqTransport>(2),
                        call.ArgAt<string>(3), call.ArgAt<SwapLinkSecret>(4), call.ArgAt<ArkAddress>(5),
                        call.ArgAt<ArkContract>(6), call.ArgAt<string?>(8), call.ArgAt<SolverCard?>(7), null);
                    IngressCalls.Add(input);
                    var facts = Ingress(input);
                    var quote = new RfqQuote<LightningReceiveQuoteProfile>
                    {
                        V = 1,
                        Type = "rfq_quote",
                        RfqId = facts.Id,
                        Pair = LightningReceiveProfile.Pair,
                        FromAmount = facts.From,
                        ToAmount = facts.To,
                        SolverPubkey = KeyHex(5),
                        ValidUntil = Now + IngressWindow,
                        RefundLocktime = Now + 21_600,
                        Profile = new()
                        {
                            PaymentHash = facts.Hash,
                            Invoice = "verified-by-ingress-client",
                            LockupAddress = facts.Contract.GetArkAddress().ToString(false),
                            SolverRefundPkScript = "5120" + KeyHex(5)
                        }
                    };
                    return new PendingLightningReceive(facts.Id, quote, quote.Profile.Invoice!, input.Secret.ExportPreimage(),
                        facts.Hash, facts.Contract, quote.Profile.LockupAddress!, facts.Payout);
                });
            var onchain = Substitute.For<IOnchainIngressQuoteClient>();
            onchain.ReceiveFromOnchainIntoAsync(default!, default, default!, default!, default!, default!, default!, default!, default, default, default)
                .ReturnsForAnyArgs(call =>
                {
                    Events.Add("onchain");
                    var input = new IngressCall(call.ArgAt<string>(0), call.ArgAt<long>(1), call.ArgAt<IRfqTransport>(2),
                        call.ArgAt<string>(3), call.ArgAt<SwapLinkSecret>(5), call.ArgAt<ArkAddress>(6),
                        call.ArgAt<ArkContract>(7), call.ArgAt<string?>(9), call.ArgAt<SolverCard?>(8), call.ArgAt<BitcoinAddress>(4));
                    IngressCalls.Add(input);
                    var facts = Ingress(input);
                    var htlc = OnchainHtlc.Derive(new uint256(Convert.FromHexString(facts.Hash), false),
                        _solver.ToXOnlyPubKey(), _merchant.ToXOnlyPubKey(), Now + 43_200, Network.RegTest);
                    var quote = new RfqQuote<OnchainReceiveQuoteProfile>
                    {
                        V = 1,
                        Type = "rfq_quote",
                        RfqId = facts.Id,
                        Pair = OnchainReceiveProfile.Pair,
                        FromAmount = facts.From,
                        ToAmount = facts.To,
                        SolverPubkey = KeyHex(5),
                        ValidUntil = Now + IngressWindow,
                        RefundLocktime = Now + 21_600,
                        Profile = new()
                        {
                            HtlcAddress = htlc.Address.ToString(),
                            HtlcLocktime = Now + 43_200,
                            MinConfirmations = 1,
                            ClaimPubkey = KeyHex(5),
                            LockupAddress = facts.Contract.GetArkAddress().ToString(false),
                            SolverRefundPkScript = "5120" + KeyHex(5)
                        }
                    };
                    return new PendingOnchainReceive(facts.Id, quote, quote.Profile.HtlcAddress!, facts.From,
                        Now + 43_200, 1, quote.Profile.LockupAddress!, facts.Hash, input.Secret.ExportPreimage(), facts.Contract, facts.Payout);
                });
            Client = new ComposedSwapClient(evm, lightning, onchain,
                new ComposedSwapOptions { OutgoingFundingSafetySeconds = 35, MinimumCheckoutWindowSeconds = 60 }, new TestClock());
        }

        private PendingEvmSend Outgoing(SwapLinkSecret secret, string id)
        {
            var refund = new ArkPaymentContract(_server.SignerKey, new Sequence(TimeSpan.FromSeconds(4096)), _merchant);
            var contract = Contract(secret, "5120" + KeyHex(7));
            var address = contract.GetArkAddress().ToString(false);
            var quote = new RfqQuote<EvmSendQuoteProfile>
            {
                V = 1,
                Type = "rfq_quote",
                RfqId = id,
                Pair = EvmSendProfile.Pair(Token),
                FromAmount = Fault == "outgoing-amount" ? 50_001 : 50_000,
                ToAmount = 123_456,
                SolverPubkey = KeyHex(5),
                ValidUntil = Now + OutgoingWindow,
                RefundLocktime = Now + 21_600,
                Profile = new()
                {
                    PaymentHash = secret.PaymentHash,
                    LockupAddress = address,
                    ReceiverPkScript = "5120" + KeyHex(7),
                    EvmTimeoutBlock = 3_700,
                    EvmRefundAddress = RefundAddress,
                    EvmContractAddress = SwapContract,
                    EvmChainId = 31_337,
                    MinConfirmations = 1,
                    MinAgeSeconds = 0
                }
            };
            return new PendingEvmSend(id, quote, secret, contract, address, refund,
                new Erc20SwapValues(secret.PaymentHash, 123_456, Token, ClaimAddress, RefundAddress, 3_700));
        }

        private (string Id, string Hash, long From, long To, string Payout, VHTLCv2Contract Contract) Ingress(IngressCall input) =>
            (Fault == "same-returned-id" ? PendingOutgoing!.RfqId : input.RfqId ?? RfqProtocol.NewRfqId(),
                Fault == "hash" ? new string('f', 64) : input.Secret.PaymentHash,
                Fault == "negative-spread" ? 49_999 : 50_150, Fault == "to" ? 50_001 : 50_000,
                Fault == "payout" ? PendingOutgoing!.RefundContract.GetArkAddress().ToString(false) : input.Payout.ToString(false),
                Contract(input.Secret, input.Payout.ScriptPubKey.ToHex()));

        private VHTLCv2Contract Contract(SwapLinkSecret secret, string payoutScript) => new(_server.SignerKey, _solver, _merchant,
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(secret.PaymentHash)), false),
            new LockTime((uint)(Now + 21_600)), new Sequence(TimeSpan.FromSeconds(4096)), new Sequence(TimeSpan.FromSeconds(4096)),
            new Sequence(TimeSpan.FromSeconds(8192)), new VHTLCv2NonInteractiveClaim(Convert.FromHexString(payoutScript), _emulator));
    }

    private static OutputDescriptor Descriptor(byte seed) => KeyExtensions.ParseOutputDescriptor(
        new Key(Enumerable.Repeat(seed, 32).ToArray()).PubKey.ToHex(), Network.RegTest);
    private static string KeyHex(byte seed) => Convert.ToHexString(Descriptor(seed).ToXOnlyPubKey().ToBytes()).ToLowerInvariant();
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }
}

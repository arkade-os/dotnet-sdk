using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.Tests.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// The off-board's two orchestration paths: negotiating and funding, and claiming on L1.
/// </summary>
/// <remarks>
/// <para>
/// The gate tests decide whether a quote is fundable and the builder tests decide what a claim looks
/// like. Neither says anything about the code that strings them together — which is where the money
/// actually moves, and which had no unit coverage at all before this fixture.
/// </para>
/// <para>
/// What is worth asserting here is not that the addresses come out right — the golden vectors and
/// the gate tests already pin that, and re-deriving them here would only prove the code agrees with
/// itself. It is the ORDER and the REFUSALS: that nothing is spent when a check says no, and that
/// the row exists before the money moves, since a crash between those two is the one failure with
/// no way back.
/// </para>
/// <para>
/// <b>What this fixture cannot reach, stated rather than left as a silent hole.</b> The send path
/// compares the L1 address before the Arkade one, and the L1 address depends on a preimage derived
/// from the wallet's own signature and salted with a per-negotiation id the client generates
/// internally. A test cannot predict it, and so cannot build a quote whose L1 address matches while
/// its lockup address does not. That one refusal is exercised end to end instead, in
/// <c>ArkadeOnchainTests</c>; everything before and after it is covered here.
/// </para>
/// </remarks>
[TestFixture]
public class OnchainSendOrchestrationTests
{
    private const long Now = 1_800_000_000;
    private const string WalletId = "wallet-1";

    // ─── Negotiating and funding ──────────────────────────────────────

    [Test]
    public void AQuoteThatFailsAGate_FundsNothing()
    {
        // The gate itself is covered elsewhere; what this pins is that a refusal reaches the caller
        // without anything having been spent, imported or recorded on the way.
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainSendNotFundableException>(() => Send(ctx, QuoteExpired()));

        // The reason is asserted, not just the type: a test that only demands "it threw" also passes
        // when the throw came from a mis-wired substitute several steps earlier, which is exactly how
        // a suite ends up green over code it never reached.
        Assert.That(ex!.Reason, Is.EqualTo(OnchainSendRefusalReason.Expired));
        AssertNothingMoved(ctx);
    }

    [Test]
    public void AQuoteNamingAnL1AddressWeDidNotDerive_FundsNothing()
    {
        // The whole safety model of this corridor: we fund only what we derived ourselves. A solver
        // that quotes any other address gets a refusal, never a funding.
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainSendNotFundableException>(
            () => Send(ctx, Quote(htlcAddress: "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Reason, Is.EqualTo(OnchainSendRefusalReason.IncompleteQuote));
            // Names both sides, so the refusal is diagnosable rather than a dead end.
            Assert.That(ex.Message, Does.Contain("our L1 HTLC derivation is"));
            Assert.That(ex.Message, Does.Contain("the solver quoted"));
        });
        AssertNothingMoved(ctx);
    }

    [Test]
    public void AQuoteMissingTheSolversClaimDestination_FundsNothing()
    {
        // `receiver_pk_script` is an INPUT to our own reconstruction, not decoration: every leaf
        // feeds the merkle root, so without it the lockup address cannot be derived at all.
        //
        // It refuses at the L1 comparison first, which is the earlier of the two — see the fixture
        // remarks on why a lockup-only mismatch is not reachable from here.
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainSendNotFundableException>(
            () => Send(ctx, Quote(receiverPkScript: null)));

        Assert.That(ex!.Reason, Is.EqualTo(OnchainSendRefusalReason.IncompleteQuote));
        AssertNothingMoved(ctx);
    }

    [Test]
    public void AQuoteWithNoAddressesAtAll_FundsNothing()
    {
        // Sending no address is already out of spec, and it must not fall back to a guessed shape —
        // that is precisely what deriving both candidates exists to avoid.
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainSendNotFundableException>(
            () => Send(ctx, Quote(htlcAddress: null, lockupAddress: null)));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Reason, Is.EqualTo(OnchainSendRefusalReason.IncompleteQuote));
            Assert.That(ex.Message, Does.Contain("(none)"), "a missing address must be named as missing");
        });
        AssertNothingMoved(ctx);
    }

    // ─── Claiming on L1 ───────────────────────────────────────────────

    [Test]
    public void AClaimOnSomethingThatIsNotAnOffBoard_IsRejected()
    {
        var ctx = Ctx(intent: Intent(type: ArkadeSwapIntentType.LightningToBtc));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Client.ClaimOnchainAsync("swap-1"));

        Assert.That(ex!.Message, Does.Contain("not an off-board"));
    }

    [Test]
    public void AClaimAtZeroConfirmations_IsRefusedOutright()
    {
        // An unconfirmed funding is one the counterparty can still replace, so this is not a knob
        // worth honouring at zero even when a caller asks.
        var ctx = Ctx(intent: Intent());

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ctx.Client.ClaimOnchainAsync("swap-1", minConfirmations: 0));
    }

    [Test]
    public async Task ARowWithNoL1Leg_IsNotAnError()
    {
        // Nothing to claim yet is the ordinary state of a freshly funded off-board, and the advance
        // pass calls this on every tick. Reporting it as a failure would make the normal case noisy.
        var ctx = Ctx(intent: Intent(withOnchainMetadata: false));

        var outcome = await ctx.Client.ClaimOnchainAsync("swap-1");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Claimed, Is.False);
            Assert.That(outcome.Txid, Is.Null);
            Assert.That(outcome.Detail, Does.Contain("not recorded"));
        });
    }

    [Test]
    public async Task AnUnfundedHtlc_IsNotAnError()
    {
        var ctx = Ctx(intent: Intent());
        ctx.Blockchain.GetUtxosAsync(default!, default).ReturnsForAnyArgs([]);

        var outcome = await ctx.Client.ClaimOnchainAsync("swap-1");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Claimed, Is.False);
            Assert.That(outcome.Detail, Does.Contain("not funded"));
        });
    }

    [Test]
    public async Task AFundingShortOfItsConfirmations_IsNotClaimed()
    {
        var ctx = Ctx(intent: Intent());
        ctx.Blockchain.GetUtxosAsync(default!, default)
            .ReturnsForAnyArgs([Utxo(100_000, height: 100)]);
        ctx.Blockchain.GetChainTime(default).ReturnsForAnyArgs(new TimeHeight(Stamp(Now), 101));

        var outcome = await ctx.Client.ClaimOnchainAsync("swap-1", minConfirmations: 6);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Claimed, Is.False);
            Assert.That(outcome.Detail, Does.Contain("confirmation"));
        });
        await ctx.Blockchain.DidNotReceiveWithAnyArgs().BroadcastAsync(default!, default);
    }

    [Test]
    public async Task AnUnderfundedHtlc_DoesNotPublishThePreimage()
    {
        // The assertion that matters most in this fixture. Claiming is not a neutral act: it puts
        // the preimage in a witness, and that is what pays the solver on the Arkade side. Claiming
        // for less than the swap promised hands over the secret for a fraction of the price.
        var ctx = Ctx(intent: Intent());
        ctx.Blockchain.GetUtxosAsync(default!, default)
            .ReturnsForAnyArgs([Utxo(10_000, height: 100)]);
        ctx.Blockchain.GetChainTime(default).ReturnsForAnyArgs(new TimeHeight(Stamp(Now), 110));

        var outcome = await ctx.Client.ClaimOnchainAsync("swap-1", minConfirmations: 1);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Claimed, Is.False);
            Assert.That(outcome.Detail, Does.Contain("less than the quoted"));
        });
        await ctx.Blockchain.DidNotReceiveWithAnyArgs().BroadcastAsync(default!, default);
    }

    [Test]
    public async Task AClaimInsideTheRefundMargin_IsDeclined()
    {
        // Broadcasting here is a race we can lose after showing our hand: a claim that does not
        // confirm before the counterparty's refund does leaves it with the sats AND our preimage,
        // which takes the Arkade side too. Declining costs only the covenant refund, still ours.
        var ctx = Ctx(intent: Intent(htlcLocktime: Now + 60), now: Now);
        ctx.Blockchain.GetUtxosAsync(default!, default)
            .ReturnsForAnyArgs([Utxo(100_000, height: 100)]);
        ctx.Blockchain.GetChainTime(default).ReturnsForAnyArgs(new TimeHeight(Stamp(Now), 110));

        var outcome = await ctx.Client.ClaimOnchainAsync("swap-1", minConfirmations: 1);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Claimed, Is.False);
            Assert.That(outcome.Detail, Does.Contain("too soon to claim safely"));
        });
        await ctx.Blockchain.DidNotReceiveWithAnyArgs().BroadcastAsync(default!, default);
    }

    // ─── Harness ──────────────────────────────────────────────────────

    private static Task<FundedOnchainSend> Send(Harness ctx, RfqQuote<OnchainSendQuoteProfile> quote)
    {
        var rfq = Substitute.For<IRfqTransport>();
        rfq.RequestQuoteAsync<OnchainSendRequestProfile, OnchainSendQuoteProfile>(default!, default)
            .ReturnsForAnyArgs(quote);

        return ctx.Client.SendToOnchainAsync(
            WalletId, PayoutAddress, 50_000, RfqAmountSide.To, rfq);
    }

    private static void AssertNothingMoved(Harness ctx)
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrowAsync(async () =>
            {
                await ctx.Spending.DidNotReceiveWithAnyArgs()
                    .Spend(default!, default(ArkTxOut[])!, default);
                await ctx.Contracts.DidNotReceiveWithAnyArgs()
                    .ImportContract(default!, default!, default, default, default);
                await ctx.Intents.DidNotReceiveWithAnyArgs()
                    .SaveArkadeSwapIntent(default!, default);
            });
        });
    }

    private sealed record Harness(
        OnchainIntentsClient Client,
        ISpendingService Spending,
        IContractService Contracts,
        IArkadeIntentStorage Intents,
        IBitcoinBlockchain Blockchain);

    private static Harness Ctx(ArkadeSwapIntent? intent = null, long now = Now)
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(default).ReturnsForAnyArgs(ServerInfo);

        var contracts = Substitute.For<IContractService>();
        contracts.DeriveContract(default!, default, default, default, default)
            .ReturnsForAnyArgs(new ArkPaymentContract(
                ServerInfo.SignerKey, new Sequence(TimeSpan.FromSeconds(4096)), ClientDescriptor));

        var spending = Substitute.For<ISpendingService>();
        spending.Spend(default!, default(ArkTxOut[])!, default).ReturnsForAnyArgs(uint256.One);

        var intents = Substitute.For<IArkadeIntentStorage>();
        intents.GetArkadeSwapIntents().ReturnsForAnyArgs(intent is null ? [] : [intent]);

        var contractStorage = Substitute.For<IContractStorage>();
        contractStorage.GetContracts().ReturnsForAnyArgs(
            intent is null ? [] : [Lockup(intent).ToEntity(WalletId)]);

        var wallets = Substitute.For<IWalletProvider>();
        wallets.GetSignerAsync(default!, default).ReturnsForAnyArgs((IArkadeWalletSigner?)null);

        var blockchain = Substitute.For<IBitcoinBlockchain>();

        var client = new OnchainIntentsClient(
            transport, contracts, spending, intents, contractStorage,
            Substitute.For<IVtxoStorage>(), wallets, blockchain,
            options: Options.Create(new ArkadeIntentsOptions()),
            time: new FixedClock(now));

        return new Harness(client, spending, contracts, intents, blockchain);
    }

    /// <summary>The lockup the claim path reads back to recover the client key.</summary>
    private static VHTLCv2Contract Lockup(ArkadeSwapIntent intent) => new(
        ServerInfo.SignerKey,
        sender: ClientDescriptor,
        receiver: Descriptor(9),
        new uint160(new byte[20], false),
        new LockTime((uint)(intent.RefundLocktime ?? Now + 86_400)),
        new Sequence(TimeSpan.FromSeconds(4096)),
        new Sequence(TimeSpan.FromSeconds(4096)),
        new Sequence(TimeSpan.FromSeconds(8192)),
        nonInteractiveClaim: new VHTLCv2NonInteractiveClaim(P2tr(2), XOnlyKey(8)),
        nonInteractiveRefund: new VHTLCv2NonInteractiveRefund(P2tr(3), XOnlyKey(8)));

    private static ArkadeSwapIntent Intent(
        ArkadeSwapIntentType type = ArkadeSwapIntentType.BtcToOnchain,
        bool withOnchainMetadata = true,
        long? htlcLocktime = null)
    {
        var intent = new ArkadeSwapIntent
        {
            Id = "swap-1",
            WalletId = WalletId,
            Type = type,
            OfferAmount = Money.Satoshis(50_000),
            WantAmount = Money.Satoshis(49_850),
            Status = ArkadeSwapIntentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            SwapPkScript = "",
            SwapAddress = "ark1lockup",
            PaymentHash = new string('d', 64),
            RefundLocktime = Now + 86_400,
        };

        // Filed under the script the lockup actually rebuilds to, which is what LoadLockupAsync
        // compares — a mismatch there is its own failure and not what these tests are about.
        intent.SwapPkScript = Lockup(intent).GetScriptPubKey().ToHex();

        return withOnchainMetadata && type == ArkadeSwapIntentType.BtcToOnchain
            ? intent.WithOnchainMetadata(new OnchainSwapMetadata(
                Convert.ToHexString(Enumerable.Repeat((byte)0xa3, 32).ToArray()).ToLowerInvariant(),
                Convert.ToHexString(XOnlyKey(7).ToBytes()).ToLowerInvariant(),
                htlcLocktime ?? Now + 6 * 60 * 60,
                PayoutAddress.ToString()))
            : intent;
    }

    private static RfqQuote<OnchainSendQuoteProfile> Quote(
        string? htlcAddress = "bcrt1p26p3wqnnngyms2s3zk8dw5xmtf2l4gpu7jh6qdr2xj3uts6m9q8qqae7nc",
        string? lockupAddress = "ark1lockup",
        string? receiverPkScript = "5120" + "bb",
        long validUntil = Now + 600) => new()
    {
        RfqId = new string('9', 64),
        Pair = OnchainSendProfile.Pair,
        FromAmount = 50_000,
        ToAmount = 49_850,
        SolverPubkey = Convert.ToHexString(XOnlyKey(5).ToBytes()).ToLowerInvariant(),
        ValidUntil = validUntil,
        RefundLocktime = Now + 12 * 60 * 60,
        Profile = new OnchainSendQuoteProfile
        {
            HtlcLocktime = Now + 6 * 60 * 60,
            MinConfirmations = 1,
            HtlcAddress = htlcAddress,
            HtlcPubkey = Convert.ToHexString(XOnlyKey(6).ToBytes()).ToLowerInvariant(),
            LockupAddress = lockupAddress,
            ReceiverPkScript = receiverPkScript is null ? null : "5120" + new string('b', 64),
        },
    };

    private static RfqQuote<OnchainSendQuoteProfile> QuoteExpired() => Quote(validUntil: Now);

    private static readonly ArkServerInfo ServerInfo =
        TestServerInfo.WithSeconds(4096);

    private static BitcoinAddress PayoutAddress =>
        BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest);

    private static OutputDescriptor ClientDescriptor => Descriptor(4);

    private static Key KeyFor(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static ECXOnlyPubKey XOnlyKey(byte seed) =>
        ECXOnlyPubKey.Create(KeyFor(seed).PubKey.TaprootInternalKey.ToBytes());

    private static byte[] P2tr(byte seed) => [0x51, 0x20, .. KeyFor(seed).PubKey.TaprootInternalKey.ToBytes()];

    private static OutputDescriptor Descriptor(byte seed) =>
        KeyExtensions.ParseOutputDescriptor(KeyFor(seed).PubKey.ToHex(), Network.RegTest);

    private static BoardingUtxo Utxo(ulong sats, long height) =>
        new(Txid: new string('7', 64), Vout: 0, Amount: sats,
            Confirmed: true, BlockHeight: height, BlockTime: Now - 3600);

    private static DateTimeOffset Stamp(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    private sealed class FixedClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}

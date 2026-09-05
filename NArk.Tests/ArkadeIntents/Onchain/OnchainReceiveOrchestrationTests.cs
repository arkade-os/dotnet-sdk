using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Tests.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// The on-board's three orchestration paths: negotiating, taking delivery, and the L1 refund.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="OnchainSendOrchestrationTests"/>, and the corridor where the client pays
/// first. That inverts what has to be proved. On the off-board the dangerous act is publishing a
/// preimage; here it is funding an L1 HTLC against a quote whose timelocks put us on the wrong side
/// of the ordering, and then failing to take that funding back.
/// </para>
/// <para>
/// The gate arithmetic lives in <c>OnchainReceiveGatesTests</c> and the transaction shapes in the
/// builder fixtures. What is asserted here is the wiring between them: that a refusal reaches the
/// caller with nothing imported or recorded, that a claim will not race a closing window, and that
/// the refund is measured against the chain's clock rather than ours.
/// </para>
/// <para>
/// <b>What this fixture cannot reach.</b> As on the send leg, the L1 address is derived from a
/// preimage the client provisions internally, so no quote written here can name the address we will
/// derive. That makes the address comparison always refuse — which is the safety property worth
/// pinning, but it also puts everything downstream of it (the lockup match, the contract import, the
/// row) out of reach from a unit test. Those are covered end to end in <c>ArkadeOnchainTests</c>.
/// </para>
/// </remarks>
[TestFixture]
public class OnchainReceiveOrchestrationTests
{
    private const long Now = 1_800_000_000;
    private const string WalletId = "wallet-1";

    /// <summary>Our own L1 deadline: the leaf that lets us take the funding back.</summary>
    private const long HtlcLocktime = Now + 12 * 60 * 60;

    // ─── Negotiating ──────────────────────────────────────────────────

    [Test]
    public void AnExpiredQuote_FundsNothing()
    {
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainReceiveNotFundableException>(
            () => Receive(ctx, Quote(validUntil: Now)));

        Assert.That(ex!.Reason, Is.EqualTo(OnchainReceiveRefusalReason.Expired));
        AssertNothingMoved(ctx);
    }

    [Test]
    public void AQuoteWhoseTimelocksOpenInTheWrongOrder_FundsNothing()
    {
        // This corridor's central safety property, and one neither contract enforces. The solver's
        // Arkade reclaim must open BEFORE our L1 refund: the other way round leaves a window in
        // which we could take the L1 sats back while still holding a claimable Arkade lockup, and
        // one leg pays for both. A swap we could only finish by robbing the counterparty is one no
        // honest solver offered, so it is refused rather than exploited.
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainReceiveNotFundableException>(
            () => Receive(ctx, Quote(arkadeRefundLocktime: HtlcLocktime)));

        Assert.That(ex!.Reason, Is.EqualTo(OnchainReceiveRefusalReason.TimelocksOutOfOrder));
        AssertNothingMoved(ctx);
    }

    [Test]
    public void AQuoteAskingForMoreConfirmationsThanTheCorridorWaitsFor_FundsNothing()
    {
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainReceiveNotFundableException>(
            () => Receive(ctx, Quote(minConfirmations: OnchainReceiveGates.MaxMinConfirmations + 1)));

        Assert.That(ex!.Reason, Is.EqualTo(OnchainReceiveRefusalReason.ConfirmationsOutOfRange));
        AssertNothingMoved(ctx);
    }

    [Test]
    public void AQuoteNamingAnL1AddressWeDidNotDerive_FundsNothing()
    {
        // We fund only what we derived ourselves. The address here is well formed and simply not
        // ours, which is exactly the case a dishonest solver would present: an HTLC whose claim leaf
        // it controls on both sides looks identical to a client that does not check.
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainReceiveNotFundableException>(
            () => Receive(ctx, Quote(htlcAddress: "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Reason, Is.EqualTo(OnchainReceiveRefusalReason.IncompleteQuote));
            // Both sides named, so the refusal is diagnosable rather than a dead end.
            Assert.That(ex.Message, Does.Contain("our L1 HTLC derivation is"));
            Assert.That(ex.Message, Does.Contain("the solver quoted"));
        });
        AssertNothingMoved(ctx);
    }

    [Test]
    public void AQuoteCarryingNoL1AddressAtAll_NamesItAsMissing()
    {
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<OnchainReceiveNotFundableException>(
            () => Receive(ctx, Quote(htlcAddress: null)));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Reason, Is.EqualTo(OnchainReceiveRefusalReason.IncompleteQuote));
            Assert.That(ex.Message, Does.Contain("(none)"), "a missing address must be named as missing");
        });
        AssertNothingMoved(ctx);
    }

    // ─── Taking delivery ──────────────────────────────────────────────

    [Test]
    public void AClaimOnSomethingThatIsNotAnOnBoard_IsRejected()
    {
        var ctx = Ctx(intent: Intent(type: ArkadeSwapIntentType.BtcToOnchain));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Client.ClaimOnchainReceiveAsync("swap-1"));

        Assert.That(ex!.Message, Does.Contain("is not an on-board"));
    }

    [Test]
    public void AClaimOnARowWithNoDeadlineRecorded_IsRejected()
    {
        // Without the solver's reclaim locktime there is no telling a safe claim from one that races
        // it, and the claim is the act that publishes the preimage. Guessing is the one option not
        // available here.
        var ctx = Ctx(intent: Intent(arkadeRefundLocktime: null));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Client.ClaimOnchainReceiveAsync("swap-1"));

        Assert.That(ex!.Message, Does.Contain("no refund locktime"));
    }

    [Test]
    public void AClaimInsideTheSolversReclaimMargin_IsRefused_AndNothingIsSpent()
    {
        // The margin, not the bare deadline. A claim that does not confirm before the solver's
        // reclaim leaves it holding its lockup AND our preimage — which takes our L1 funding too.
        // Both legs, for the sake of a few minutes.
        var ctx = Ctx(intent: Intent(arkadeRefundLocktime: Now + 60));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Client.ClaimOnchainReceiveAsync("swap-1"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("before the solver's Arkade reclaim opens"));
            // The recourse is named, so a caller reading this refunds instead of retrying.
            Assert.That(ex.Message, Does.Contain("L1 refund is the way out"));
        });
        Assert.DoesNotThrowAsync(async () =>
            await ctx.Spending.DidNotReceiveWithAnyArgs().Spend(default!, default(ArkTxOut[])!, default));
    }

    // ─── The L1 refund ────────────────────────────────────────────────

    [Test]
    public void ARefundOnAnUnknownSwap_IsRejected()
    {
        var ctx = Ctx();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Client.RefundOnchainReceiveAsync("swap-1"));

        Assert.That(ex!.Message, Does.Contain("is unknown"));
    }

    [Test]
    public async Task ARefundOnceWeHavePublishedThePreimage_IsRefused()
    {
        // Past Fulfilled the preimage is public, so the solver can take this same HTLC. Racing it is
        // a wasted fee if we lose and an attempt to collect both legs if we win — neither is
        // something an advance pass nobody is watching should do on its own.
        var ctx = Ctx(intent: Intent(status: ArkadeSwapIntentStatus.Fulfilled));

        var outcome = await ctx.Client.RefundOnchainReceiveAsync("swap-1");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Refunded, Is.False);
            Assert.That(outcome.Detail, Does.Contain("not ours to take back"));
        });
        await ctx.Blockchain.DidNotReceiveWithAnyArgs().BroadcastAsync(default!, default);
    }

    [Test]
    public async Task ARefundOnARowWithNoL1Leg_IsNotAnError()
    {
        // The advance pass calls this on every tick, rows whose L1 leg was never recorded included.
        // Reporting that as a failure makes the ordinary case noisy.
        var ctx = Ctx(intent: Intent(withOnchainMetadata: false));

        var outcome = await ctx.Client.RefundOnchainReceiveAsync("swap-1");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Refunded, Is.False);
            Assert.That(outcome.Detail, Does.Contain("not recorded"));
        });
    }

    [Test]
    public async Task AnL1HtlcHoldingNothingConfirmed_IsNotAnError()
    {
        var ctx = Ctx(intent: Intent());
        ctx.Blockchain.GetUtxosAsync(default!, default).ReturnsForAnyArgs([]);

        var outcome = await ctx.Client.RefundOnchainReceiveAsync("swap-1");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Refunded, Is.False);
            Assert.That(outcome.Detail, Does.Contain("nothing confirmed"));
        });
        await ctx.Blockchain.DidNotReceiveWithAnyArgs().BroadcastAsync(default!, default);
    }

    [Test]
    public async Task ARefundIsMeasuredAgainstMedianTimePast_NotTheLocalClock()
    {
        // The sharpest assertion here. Our own clock is set an hour past the locktime while the
        // chain's median time past is not — the ordinary state of affairs, since MTP is the median
        // of the last eleven block times and trails wall clock. A refund built against the local
        // clock is well formed and rejected as non-final, with nothing in the rejection saying why,
        // so getting this wrong surfaces as an unexplained broadcast failure rather than "not yet".
        var ctx = Ctx(intent: Intent(), now: HtlcLocktime + 3600);
        ctx.Blockchain.GetUtxosAsync(default!, default).ReturnsForAnyArgs([Utxo(100_000)]);
        ctx.Blockchain.GetChainTime(default)
            .ReturnsForAnyArgs(new TimeHeight(Stamp(HtlcLocktime - 1), 200));

        var outcome = await ctx.Client.RefundOnchainReceiveAsync("swap-1");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Refunded, Is.False);
            Assert.That(outcome.Detail, Does.Contain("median time past"));
        });
        await ctx.Blockchain.DidNotReceiveWithAnyArgs().BroadcastAsync(default!, default);
    }

    [Test]
    public async Task AMaturedRefund_IsBroadcastToTheRecordedAddress_AndCancelsTheRow()
    {
        // The success path. With only refusals pinned, a refund that signed for the wrong key, paid
        // the wrong address, or left the row in flight would keep the fixture green while no
        // on-board could ever be taken back.
        var ctx = Ready();

        var outcome = await ctx.Client.RefundOnchainReceiveAsync("swap-1");

        var broadcast = OnlyBroadcast(ctx);
        var saved = LastSavedIntent(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Refunded, Is.True);
            Assert.That(outcome.Txid, Is.EqualTo(broadcast.GetHash().ToString()),
                "the reported txid must be the transaction that was actually broadcast");
            Assert.That(broadcast.Outputs[0].ScriptPubKey, Is.EqualTo(RefundAddress.ScriptPubKey),
                "the refund must pay the address the row recorded");
            Assert.That(saved!.Status, Is.EqualTo(ArkadeSwapIntentStatus.Cancelled),
                "a row left in flight is one the advance pass keeps trying to refund");
        });
    }

    [Test]
    public async Task ARefundTheNetworkRejects_LeavesTheRowAlone()
    {
        // Nothing moved, so nothing may be recorded as having moved. Marking the row Cancelled here
        // would stop the advance pass retrying a refund that never reached a mempool.
        var ctx = Ready();
        ctx.Blockchain.BroadcastAsync(default!, default).ReturnsForAnyArgs(false);

        var outcome = await ctx.Client.RefundOnchainReceiveAsync("swap-1");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Refunded, Is.False);
            Assert.That(outcome.Detail, Does.Contain("not accepted"));
        });
        Assert.DoesNotThrowAsync(async () =>
            await ctx.Intents.DidNotReceiveWithAnyArgs().SaveArkadeSwapIntent(default!, default));
    }

    [Test]
    public async Task ARefundHonoursAnOverrideAddress_OverTheRecordedOne()
    {
        // The override exists for a wallet whose recorded destination is no longer somewhere it can
        // spend from — a restored backup, a rotated account. Preferring the stored address anyway
        // would send the money exactly where the caller said not to.
        var elsewhere = BitcoinAddress.Create(
            "bcrt1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3qzf4jry", Network.RegTest);
        var ctx = Ready();

        await ctx.Client.RefundOnchainReceiveAsync("swap-1", elsewhere);

        Assert.That(OnlyBroadcast(ctx).Outputs[0].ScriptPubKey, Is.EqualTo(elsewhere.ScriptPubKey));
    }

    // ─── Harness ──────────────────────────────────────────────────────

    /// <summary>A context whose refund would go through: funded, matured, signable, accepted.</summary>
    private static Harness Ready()
    {
        var ctx = Ctx(intent: Intent());
        ctx.Blockchain.GetUtxosAsync(default!, default).ReturnsForAnyArgs([Utxo(100_000)]);
        ctx.Blockchain.GetChainTime(default)
            .ReturnsForAnyArgs(new TimeHeight(Stamp(HtlcLocktime), 200));
        ctx.Blockchain.EstimateFeeRateAsync(default, default)
            .ReturnsForAnyArgs(new FeeRate(Money.Satoshis(2), 1));
        ctx.Blockchain.BroadcastAsync(default!, default).ReturnsForAnyArgs(true);

        // Built into a local first: NSubstitute refuses a substitute configured inside another's
        // Returns(...), and the exception it raises names neither call.
        var signer = SignerFor(ReceiverKey);
        ctx.Wallets.GetSignerAsync(default!, default).ReturnsForAnyArgs(signer);

        return ctx;
    }

    private static Task<PendingOnchainReceive> Receive(
        Harness ctx, RfqQuote<OnchainReceiveQuoteProfile> quote)
    {
        var rfq = Substitute.For<IRfqTransport>();
        rfq.RequestQuoteAsync<OnchainReceiveRequestProfile, OnchainReceiveQuoteProfile>(default!, default)
            .ReturnsForAnyArgs(quote);

        return ctx.Client.ReceiveFromOnchainAsync(
            WalletId, 50_000, rfq, CovclaimdPubKey, RefundAddress);
    }

    /// <summary>The one transaction handed to <see cref="IBitcoinBlockchain.BroadcastAsync"/>.</summary>
    /// <remarks>
    /// Read off the recorded call rather than captured with <c>Arg.Do</c>, which only fires while
    /// the call is being made: written into a <c>Received()</c> assertion afterwards it leaves the
    /// variable null and the test dies on a NullReferenceException that explains nothing.
    /// </remarks>
    private static Transaction OnlyBroadcast(Harness ctx)
    {
        var calls = ctx.Blockchain.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBitcoinBlockchain.BroadcastAsync))
            .ToList();

        Assert.That(calls, Has.Count.EqualTo(1), "expected exactly one broadcast");
        return (Transaction)calls[0].GetArguments()[0]!;
    }

    private static ArkadeSwapIntent? LastSavedIntent(Harness ctx) =>
        ctx.Intents.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IArkadeIntentStorage.SaveArkadeSwapIntent))
            .Select(c => (ArkadeSwapIntent)c.GetArguments()[0]!)
            .LastOrDefault();

    private static void AssertNothingMoved(Harness ctx)
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrowAsync(async () =>
            {
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
        IBitcoinBlockchain Blockchain,
        IWalletProvider Wallets);

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

        return new Harness(client, spending, contracts, intents, blockchain, wallets);
    }

    /// <summary>The lockup the refund path reads back to recover our key.</summary>
    /// <remarks>
    /// On this leg the covenant's <c>receiver</c> is ours — we are the one claiming — and it is the
    /// same key the L1 refund leaf commits to. The send leg's mirror reads <c>sender</c> instead,
    /// and getting the two the wrong way round is a refund nothing can sign.
    /// </remarks>
    private static VHTLCv2Contract Lockup(ArkadeSwapIntent intent) => new(
        ServerInfo.SignerKey,
        sender: Descriptor(4),
        receiver: ReceiverDescriptor,
        new uint160(new byte[20], false),
        new LockTime((uint)(intent.RefundLocktime ?? Now + 86_400)),
        new Sequence(TimeSpan.FromSeconds(4096)),
        new Sequence(TimeSpan.FromSeconds(4096)),
        new Sequence(TimeSpan.FromSeconds(8192)),
        nonInteractiveClaim: new VHTLCv2NonInteractiveClaim(P2tr(2), XOnlyKey(8)),
        nonInteractiveRefund: new VHTLCv2NonInteractiveRefund(P2tr(3), XOnlyKey(8)));

    private static ArkadeSwapIntent Intent(
        ArkadeSwapIntentType type = ArkadeSwapIntentType.OnchainToBtc,
        ArkadeSwapIntentStatus status = ArkadeSwapIntentStatus.Pending,
        bool withOnchainMetadata = true,
        long? arkadeRefundLocktime = Now + 86_400)
    {
        var intent = new ArkadeSwapIntent
        {
            Id = "swap-1",
            WalletId = WalletId,
            Type = type,
            OfferAmount = Money.Satoshis(50_000),
            WantAmount = Money.Satoshis(49_850),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            SwapPkScript = "",
            SwapAddress = "ark1lockup",
            PaymentHash = new string('d', 64),
            RefundLocktime = arkadeRefundLocktime,
        };

        // Filed under the script the lockup actually rebuilds to, which is what LoadLockupAsync
        // compares — a mismatch there is its own failure and not what these tests are about.
        intent.SwapPkScript = Lockup(intent).GetScriptPubKey().ToHex();

        return withOnchainMetadata && type == ArkadeSwapIntentType.OnchainToBtc
            ? intent.WithOnchainMetadata(new OnchainSwapMetadata(
                Convert.ToHexString(Enumerable.Repeat((byte)0xa3, 32).ToArray()).ToLowerInvariant(),
                Convert.ToHexString(XOnlyKey(7).ToBytes()).ToLowerInvariant(),
                HtlcLocktime,
                RefundAddress.ToString()))
            : intent;
    }

    private static RfqQuote<OnchainReceiveQuoteProfile> Quote(
        string? htlcAddress = "bcrt1p26p3wqnnngyms2s3zk8dw5xmtf2l4gpu7jh6qdr2xj3uts6m9q8qqae7nc",
        long validUntil = Now + 600,
        long arkadeRefundLocktime = Now + 6 * 60 * 60,
        long htlcLocktime = HtlcLocktime,
        int minConfirmations = 1) => new()
    {
        RfqId = new string('9', 64),
        Pair = OnchainReceiveProfile.Pair,
        FromAmount = 50_000,
        ToAmount = 49_850,
        SolverPubkey = Convert.ToHexString(XOnlyKey(5).ToBytes()).ToLowerInvariant(),
        ValidUntil = validUntil,
        RefundLocktime = arkadeRefundLocktime,
        Profile = new OnchainReceiveQuoteProfile
        {
            HtlcLocktime = htlcLocktime,
            MinConfirmations = minConfirmations,
            HtlcAddress = htlcAddress,
            ClaimPubkey = Convert.ToHexString(XOnlyKey(6).ToBytes()).ToLowerInvariant(),
            LockupAddress = "ark1lockup",
            SolverRefundPkScript = "5120" + new string('b', 64),
        },
    };

    /// <summary>A signer that really signs, with the key the covenant names as <c>receiver</c>.</summary>
    /// <remarks>
    /// A stub handing back a fixed 64 bytes would carry these tests just as far, and that is the
    /// problem: signing for the wrong key is one of the failures the success path exists to catch,
    /// and it stays invisible until the network rejects a transaction already called refunded.
    /// </remarks>
    private static IArkadeWalletSigner SignerFor(ECPrivKey key)
    {
        var signer = Substitute.For<IArkadeWalletSigner>();
        signer.Sign(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            var hash = call.ArgAt<uint256>(1);
            return Task.FromResult((key.CreateXOnlyPubKey(), key.SignBIP340(hash.ToBytes(false))));
        });
        return signer;
    }

    private static readonly ArkServerInfo ServerInfo = TestServerInfo.WithSeconds(4096);

    /// <summary>covclaimd's key, compressed secp256k1 — what the claim packet is sealed to.</summary>
    private static string CovclaimdPubKey => KeyFor(11).PubKey.Compress().ToHex();

    private static BitcoinAddress RefundAddress =>
        BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest);

    private static OutputDescriptor ClientDescriptor => Descriptor(4);
    private static OutputDescriptor ReceiverDescriptor => Descriptor(9);
    private static ECPrivKey ReceiverKey => ECPrivKey.Create(KeyFor(9).ToBytes());

    private static Key KeyFor(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static ECXOnlyPubKey XOnlyKey(byte seed) =>
        ECXOnlyPubKey.Create(KeyFor(seed).PubKey.TaprootInternalKey.ToBytes());

    private static byte[] P2tr(byte seed) => [0x51, 0x20, .. KeyFor(seed).PubKey.TaprootInternalKey.ToBytes()];

    private static OutputDescriptor Descriptor(byte seed) =>
        KeyExtensions.ParseOutputDescriptor(KeyFor(seed).PubKey.ToHex(), Network.RegTest);

    private static BoardingUtxo Utxo(ulong sats) =>
        new(Txid: new string('7', 64), Vout: 0, Amount: sats,
            Confirmed: true, BlockHeight: 100, BlockTime: Now - 3600);

    private static DateTimeOffset Stamp(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    private sealed class FixedClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}

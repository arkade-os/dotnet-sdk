using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Services;
using NArk.Blockchain;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Arkade;

/// <summary>
/// Both onchain corridors driven all the way through settlement, against a live solver.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="ArkadeLightningTests"/>, and it closes the same gap. Golden vectors
/// pin the covenant and <c>OnchainReceiveGatesTests</c> pins the deadline arithmetic, but both stop
/// at "we would fund the right script". What neither can show is that the funding, the fill and the
/// claim actually cross two chains and settle — and this corridor has more places to get that wrong
/// than the Lightning one, because both legs are chains this SDK has to watch and spend on itself.
/// </para>
/// <para>
/// <b>The solver is not part of the regtest stack and has to be started by hand.</b> Point
/// <c>ARKADE_LN_SOLVER_URL</c> at it — the same solver serves every corridor — and without that
/// variable every test here skips, which is also what happens in CI.
/// </para>
/// <para>
/// They also skip when the solver answers <c>unsupported_pair</c>. The onchain corridors are
/// separately switchable in a solver deployment (<c>ONCHAIN_SEND</c> / <c>ONCHAIN_RECEIVE</c>), so a
/// solver built without them is a configuration this suite has nothing to say about — failing red
/// there would report a corridor bug where there is only a corridor that was not turned on.
/// </para>
/// <para>
/// Unlike the Lightning suite these need no Lightning node: the far leg is Bitcoin L1, driven
/// through <c>bitcoin-cli</c> in the regtest stack. What they do need is an
/// <see cref="IBitcoinBlockchain"/>, which is also what the corridor itself needs and what a
/// Lightning-only deployment has no reason to register.
/// </para>
/// <para>
/// Kept out of the automated run by category rather than only by the environment guard, for the
/// reason <see cref="ArkadeLightningTests"/> gives: a skip claims the corridor was considered and
/// found untestable, and CI has no solver to consider it with. Select them deliberately with
/// <c>--filter TestCategory=OnchainCorridors</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("OnchainCorridors")]
[Category("ArkadeIntents")]
public class ArkadeOnchainTests
{
    private const long SwapSats = 50_000;

    private static readonly Uri EmulatorEndpoint = new("http://localhost:7073");

    /// <summary>How long the solver gets to fund its side, and the chain to show it.</summary>
    /// <remarks>
    /// Sized for a slow regtest rather than a healthy one. Both directions cross a confirmation wait
    /// the solver itself quotes, so the failure this must not produce is a timeout that reads like a
    /// protocol bug when it was only a slow block.
    /// </remarks>
    private static readonly TimeSpan SolverTimeout = TimeSpan.FromMinutes(3);

    // ─── arkade:BTC -> onchain:BTC ────────────────────────────────────

    /// <summary>
    /// Off-board an Arkade balance: fund the covenant, let the solver fund the L1 HTLC, claim it,
    /// and let the preimage that claim published pay the solver on Arkade.
    /// </summary>
    /// <remarks>
    /// The last assertion is the one that matters. Only a solver that could both fund L1 and spend
    /// our covenant can produce this end state, so L1 sats in our own wallet plus a spent lockup is
    /// the whole corridor confirmed at once — including the preimage crossing between the rails,
    /// which nothing short of settlement demonstrates.
    /// </remarks>
    [Test]
    public async Task OffBoard_FundsTheCovenant_ThenClaimsTheSolversL1Htlc()
    {
        var ctx = await SetUpAsync();
        var payout = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(), Network.RegTest);

        var funded = await Quoted(() => ctx.Intents.SendToOnchainAsync(
            ctx.WalletId, payout, SwapSats, ctx.Rfq, RfqAmountSide.To));

        Assert.Multiple(() =>
        {
            Assert.That(funded.FundingTxid, Is.Not.Empty);
            // SendToOnchainAsync refuses before funding if either derivation disagrees, so reaching
            // here already means both matched — asserted anyway so a regression names itself.
            Assert.That(funded.LockupAddress, Is.EqualTo(funded.Quote.Profile!.LockupAddress));
            Assert.That(funded.HtlcAddress, Is.EqualTo(funded.Quote.Profile.HtlcAddress));
        });

        // The solver funds L1 only after our Arkade lockup is visible to it.
        var htlcFunded = await Poll(
            async () => (await ctx.Blockchain.GetUtxosAsync(funded.HtlcAddress)).Count > 0, SolverTimeout);
        Assert.That(htlcFunded, Is.True, "the solver funded the L1 HTLC it quoted");

        // Its own quoted count, not a guess: the corridor refuses to claim before it is met.
        await DockerHelper.MineBlocks(funded.Quote.Profile!.MinConfirmations!.Value);

        var claimed = await Poll(async () =>
            (await ctx.Intents.AdvanceAsync(funded.RfqId)).Acted, SolverTimeout);
        Assert.That(claimed, Is.True, "the advance pass claimed the L1 HTLC once it was confirmed enough");

        var taken = await Poll(async () =>
            (await GetVtxo(ctx, funded.LockupPkScript))?.SpentByTransactionId is { Length: > 0 }, SolverTimeout);
        Assert.That(taken, Is.True, "the solver took the covenant with the preimage our L1 claim published");

        // Reconciliation is what a restarted client runs, so driving the end state through it checks
        // the observation path rather than only the chain. Fulfilled, not Resolved: the preimage in
        // the spending witness is what tells a fill from a refund, and reading it is the part of
        // this corridor that used to be missing.
        var reconciled = await ctx.Intents.ReconcileAsync();
        Assert.That(
            reconciled.Updated.SingleOrDefault(u => u.SwapId == funded.RfqId)?.To,
            Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    // ─── onchain:BTC -> arkade:BTC ────────────────────────────────────

    /// <summary>
    /// On-board L1 sats: fund the HTLC we derived, let the solver fund the Arkade covenant against
    /// it, and claim that covenant with the preimage we chose.
    /// </summary>
    /// <remarks>
    /// The exposure is the mirror of the off-board's, and so is the thing worth proving: here the
    /// solver pays out first and is only repaid when our claim publishes the secret. A lockup that
    /// appears at the address we derived — before we have revealed anything — is that inversion
    /// working.
    /// </remarks>
    [Test]
    public async Task OnBoard_FundsTheL1Htlc_ThenClaimsTheSolversCovenant()
    {
        var ctx = await SetUpAsync();
        var refundTo = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(), Network.RegTest);

        var covclaimd = await ctx.ReadCovclaimdPubKeyAsync();

        var pending = await Quoted(() => ctx.Intents.ReceiveFromOnchainAsync(
            ctx.WalletId, SwapSats, ctx.Rfq, covclaimd, refundTo, RfqAmountSide.From));

        Assert.Multiple(() =>
        {
            Assert.That(pending.FundAmountSats, Is.EqualTo(SwapSats));
            Assert.That(pending.HtlcAddress, Is.EqualTo(pending.Quote.Profile!.HtlcAddress));
            Assert.That(pending.LockupAddress, Is.EqualTo(pending.Quote.Profile.LockupAddress));
        });

        // Fund the address WE derived, never the one the quote names — they were just asserted equal,
        // and using ours is what makes that assertion load-bearing rather than decorative.
        await DockerHelper.BitcoinSendToAddress(pending.HtlcAddress, Money.Satoshis(pending.FundAmountSats));
        await DockerHelper.MineBlocks(pending.MinConfirmations);

        var lockup = await WaitForVtxo(ctx, pending.Contract.GetScriptPubKey().ToHex());
        Assert.That(lockup.Amount, Is.EqualTo((ulong)pending.Quote.ToAmount),
            "the solver funded the covenant for the amount it quoted, before we revealed anything");

        var claimed = await ctx.Intents.ClaimOnchainReceiveAsync(pending.RfqId);
        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
            Assert.That(claimed.SpentTxid, Is.Not.Null.And.Not.Empty);
        });

        // The solver's own collection: our claim put the preimage in a witness, and that is the only
        // way it can take the L1 HTLC we funded.
        var settled = await Poll(
            async () => (await ctx.Blockchain.GetUtxosAsync(pending.HtlcAddress)).Count == 0, SolverTimeout);
        Assert.That(settled, Is.True, "the solver claimed the L1 HTLC with the preimage we published");
    }

    /// <summary>
    /// The on-board's refund answers "not yet" while its leaf is immature, rather than broadcasting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refund itself cannot be driven here: the leaf opens hours out by construction, and
    /// winding regtest forward far enough would move the median time past under every other contract
    /// the stack is holding. What CAN be tested is the gate, and it is the half worth testing — a
    /// refund built against the local clock is well formed and rejected as non-final, because
    /// consensus matures CLTV against median time past, which trails wall clock by about an hour.
    /// </para>
    /// <para>
    /// So this pins the boring answer against a funded, live HTLC: not refused for want of a record,
    /// not broadcast, but declined by the clock the chain actually uses.
    /// </para>
    /// </remarks>
    [Test]
    public async Task OnBoard_RefundIsDeclinedUntilItsLeafMatures()
    {
        var ctx = await SetUpAsync();
        var refundTo = BitcoinAddress.Create(await DockerHelper.BitcoinGetNewAddress(), Network.RegTest);

        var covclaimd = await ctx.ReadCovclaimdPubKeyAsync();

        var pending = await Quoted(() => ctx.Intents.ReceiveFromOnchainAsync(
            ctx.WalletId, SwapSats, ctx.Rfq, covclaimd, refundTo, RfqAmountSide.From));

        await DockerHelper.BitcoinSendToAddress(pending.HtlcAddress, Money.Satoshis(pending.FundAmountSats));
        await DockerHelper.MineBlocks(1);

        var outcome = await ctx.Intents.RefundOnchainReceiveAsync(pending.RfqId);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Refunded, Is.False);
            Assert.That(outcome.Txid, Is.Null, "nothing may be broadcast before the leaf opens");
            // Names the leaf's deadline, not a missing row — the two failures read identically to a
            // caller otherwise, and only one of them means "wait".
            Assert.That(outcome.Detail, Does.Contain("median time past"));
        });
    }

    // ─── Harness ──────────────────────────────────────────────────────

    private sealed record Ctx(
        string WalletId,
        IClientTransport Transport,
        ArkadeIntentsService Intents,
        IRfqTransport Rfq,
        IBitcoinBlockchain Blockchain,
        Uri CovclaimdUrl,
        IVtxoStorage VtxoStorage)
    {
        /// <summary>Reads covclaimd's key from its own endpoint rather than hardcoding it.</summary>
        /// <remarks>
        /// covclaimd generates its key at startup, so a rebuilt stack invalidates any copy. A stale
        /// one seals the preimage to a daemon that cannot open it, which nothing on the wire notices
        /// — the swap simply loses its offline claim path.
        /// </remarks>
        public async Task<string> ReadCovclaimdPubKeyAsync()
        {
            using var http = new HttpClient { BaseAddress = CovclaimdUrl };
            var doc = await http.GetFromJsonAsync<JsonElement>("v1/preimage/covclaimd-pubkey");
            return doc.GetProperty("covclaimd_pub_key").GetString()!;
        }
    }

    /// <summary>
    /// Run a negotiation, turning a solver that does not serve the pair into a skip.
    /// </summary>
    /// <remarks>
    /// The onchain corridors are separately switchable in a deployment, so `unsupported_pair` means
    /// "not turned on here", not "broken". Every other refusal reason is a real answer and is left
    /// to fail the test.
    /// </remarks>
    private static async Task<T> Quoted<T>(Func<Task<T>> negotiate)
    {
        try
        {
            return await negotiate();
        }
        catch (RfqRefusedException e) when (e.Reason == RfqRefusalReason.UnsupportedPair)
        {
            Assert.Ignore(
                "the solver does not serve this onchain pair — the corridor is switchable per " +
                "deployment, so this is a configuration these tests have nothing to say about.");
            throw;
        }
    }

    private static async Task<Ctx> SetUpAsync()
    {
        var solverUrl = Env("ARKADE_LN_SOLVER_URL");
        if (solverUrl is null)
        {
            Assert.Ignore(
                "needs a solver started by hand — set ARKADE_LN_SOLVER_URL. " +
                "The solver is not part of the regtest stack.");
        }

        var w = await FundedWalletHelper.GetFundedWallet();

        var emulator = new EmulatorClient(new HttpClient(),
            Options.Create(new EmulatorClientOptions { ServerUrl = EmulatorEndpoint.ToString() }));

        // PaymentContractTransformer spends the wallet's own coins to fund a lockup;
        // ArkProgramContractTransformer spends the covenant itself on claim and refund.
        var coinService = new CoinService(w.clientTransport, w.contracts,
            [new PaymentContractTransformer(w.walletProvider), new ArkProgramContractTransformer(w.walletProvider)]);

        var spendingService = new SpendingService(
            w.vtxoStorage, w.contracts, coinService, w.walletProvider, w.contractService, w.clientTransport,
            new NArk.Core.CoinSelector.DefaultCoinSelector(), w.safetyService, TestStorage.CreateIntentStorage(),
            postSpendEventHandlers: [], logger: null,
            extensionPacketProviders: [new ArkadeEmulatorPacketProvider()],
            submitHandlers: [new ArkadeEmulatorSpendSubmitter(emulator, new PrevArkTxProvider(w.clientTransport))]);

        var intentStorage = new InMemoryArkadeIntentStorage();
        var blockchain = new EsploraBlockchain(SharedArkInfrastructure.ChopsticksEndpoint);

        var onchain = new OnchainIntentsClient(
            w.clientTransport, w.contractService, spendingService, intentStorage,
            w.contracts, w.vtxoStorage, w.walletProvider, blockchain);

        // Neither Lightning nor the asset corridor is exercised here, so their clients are left out
        // rather than constructed to satisfy a signature.
        var intents = new ArkadeIntentsService(
            assets: null!,
            lightning: null!,
            intentStorage: intentStorage,
            vtxoStorage: w.vtxoStorage,
            transport: w.clientTransport,
            onchain: onchain,
            time: TimeProvider.System);

        var solver = new Uri(solverUrl!.EndsWith('/') ? solverUrl : solverUrl + "/");
        var rfq = new HttpRfqTransport(new HttpClient(), solver);
        var covclaimd = new Uri(Env("ARKADE_COVCLAIMD_URL") ?? "http://localhost:7271");

        return new Ctx(w.walletIdentifier, w.clientTransport, intents, rfq, blockchain, covclaimd, w.vtxoStorage);
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static async Task<ArkVtxo?> GetVtxo(Ctx ctx, string scriptHex)
    {
        await foreach (var vtxo in ctx.Transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { scriptHex }))
        {
            return vtxo;
        }
        return null;
    }

    private static async Task<ArkVtxo> WaitForVtxo(Ctx ctx, string scriptHex)
    {
        var deadline = DateTimeOffset.UtcNow + SolverTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await GetVtxo(ctx, scriptHex) is { } vtxo) return vtxo;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"nothing appeared at {scriptHex} within {SolverTimeout}");
    }

    private static async Task<bool> Poll(Func<Task<bool>> condition, TimeSpan within)
    {
        var deadline = DateTimeOffset.UtcNow + within;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(1000);
        }
        return false;
    }
}

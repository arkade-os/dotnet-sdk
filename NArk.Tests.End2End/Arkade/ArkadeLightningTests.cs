using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Services;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;

namespace NArk.Tests.End2End.Arkade;

/// <summary>
/// The Lightning corridors driven all the way through settlement, against a live solver.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about these corridors is checked without money moving: golden vectors pin the
/// script construction, and <c>LiveQuoteDerivationTests</c> replays a captured quote to prove we
/// derive the address a real solver quoted. Both stop at the same place — they show we would fund
/// the right script, not that the funding, the fill and the claim actually work end to end. That
/// gap is what this file closes, and until it runs there is a difference between "verified at the
/// contract" and "known to settle."
/// </para>
/// <para>
/// <b>The solver is not part of the regtest stack and has to be started by hand.</b> Point
/// <c>ARKADE_LN_SOLVER_URL</c> at it; without that variable every test here skips, which is also
/// what happens in CI, where no solver runs.
/// </para>
/// <para>
/// The tests also need a Lightning node — the send leg needs an invoice for the solver to pay, and
/// the receive leg needs someone to settle the invoice the solver mints. They drive one through
/// <c>docker exec … lncli</c> rather than over LND's REST port, which is deliberate: the stack does
/// not publish that port, and reaching it would mean copying a TLS certificate and macaroon that
/// every <c>--clean</c> silently replaces. A stale copy fails as a TLS or permission error that
/// reads exactly like a client bug. Talking to the container has neither problem.
/// <c>ARKADE_LND_CONTAINER</c> overrides the container name, and
/// <c>ARKADE_COVCLAIMD_URL</c> the daemon's own address.
/// </para>
/// <para>
/// Kept out of CI by category, not just by the environment guard below. The guard alone would make
/// these skip there, but a skip is a claim that the corridor was considered and found untestable,
/// and CI has no solver to consider it with. Excluding them says the true thing: this suite is not
/// part of the automated run yet. Select it deliberately with
/// <c>--filter TestCategory=LightningCorridors</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("LightningCorridors")]
public class ArkadeLightningTests
{
    private const long SwapSats = 50_000;

    private static readonly Uri EmulatorEndpoint = new("http://localhost:7073");

    /// <summary>How long a solver gets to pay an invoice and claim, or to fund its side.</summary>
    /// <remarks>
    /// Both directions cross a real Lightning payment plus an Arkade spend, so this is sized for a
    /// slow regtest rather than a healthy one — the failure it must not produce is a timeout that
    /// looks like a protocol bug.
    /// </remarks>
    private static readonly TimeSpan SolverTimeout = TimeSpan.FromMinutes(3);

    // ─── arkade:BTC -> lightning:BTC ──────────────────────────────────

    /// <summary>
    /// Pay a real invoice out of an Arkade balance: fund the lockup, and let the solver settle the
    /// invoice and take the lockup by revealing the preimage.
    /// </summary>
    /// <remarks>
    /// The assertion that matters is the last one. Anything can lock sats into an address; only a
    /// solver that could both settle the invoice and spend our script can produce this end state,
    /// so a spent lockup plus a settled invoice is the whole corridor confirmed at once.
    /// </remarks>
    [Test]
    public async Task Send_FundsTheLockup_AndTheSolverClaimsItByPayingTheInvoice()
    {
        var ctx = await SetUpAsync();
        var invoice = ctx.Lnd.CreateInvoice(SwapSats, "arkade send e2e");

        var funded = await ctx.Intents.SendToLightningAsync(ctx.WalletId, invoice.Bolt11, ctx.Rfq);

        Assert.Multiple(() =>
        {
            Assert.That(funded.FundedSats, Is.EqualTo(SwapSats));
            Assert.That(funded.FundingTxid, Is.Not.Empty);
            // SendToLightningAsync refuses before funding if this does not match, so reaching here
            // already means the derivation agreed — asserted anyway so a regression names itself.
            Assert.That(funded.LockupAddress, Is.EqualTo(funded.Quote.Profile!.LockupAddress));
        });

        var lockup = await WaitForVtxo(ctx, funded.LockupPkScript);
        Assert.That(lockup.Amount, Is.EqualTo((ulong)SwapSats), "the lockup holds the quoted amount");

        var settled = await Poll(() => Task.FromResult(ctx.Lnd.IsSettled(invoice.PaymentHash)), SolverTimeout);
        Assert.That(settled, Is.True, "the solver settled the invoice it was paid to settle");

        var taken = await Poll(async () => (await GetVtxo(ctx, funded.LockupPkScript))?.SpentByTransactionId is
            { Length: > 0 }, SolverTimeout);
        Assert.That(taken, Is.True, "the solver claimed the lockup with the preimage it learned");

        // Reconciliation is what a restarted client would run, so driving the end state through it
        // checks the observation path too rather than only the chain.
        var reconciled = await ctx.Intents.ReconcileAsync();
        Assert.That(
            reconciled.Updated.SingleOrDefault(u => u.SwapId == funded.RfqId)?.To,
            Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    // ─── lightning:BTC -> arkade:BTC ──────────────────────────────────

    /// <summary>
    /// Be paid over Lightning: hand out the solver's invoice, settle it, and claim the Arkade side.
    /// </summary>
    /// <remarks>
    /// The corridor's asymmetry is the point here — the solver funds Arkade before the payment it is
    /// owed has settled, and our claim is what releases the preimage that pays it. A test that
    /// stopped at "the lockup appeared" would miss that the claim has to actually validate.
    /// </remarks>
    [Test]
    public async Task Receive_TakesDeliveryOnArkade_AfterThePayerSettles()
    {
        var ctx = await SetUpAsync();
        var covclaimdKey = await ctx.ReadCovclaimdPubKeyAsync();

        var pending = await ctx.Intents.ReceiveFromLightningAsync(
            ctx.WalletId, SwapSats, ctx.Rfq, covclaimdKey);

        Assert.That(pending.Invoice, Is.Not.Empty, "the solver minted an invoice for the payer");

        // The solver pays out first on this corridor, so it needs float it is not already settling.
        // Between the quote and the payment is the only window where we know its script and it has
        // not yet been asked to fund.
        if (pending.Quote.Profile?.SolverRefundPkScript is { Length: > 0 } solverScript)
        {
            var serverInfo = await ctx.Transport.GetServerInfoAsync();
            if (!await SolverLiquidityHelper.EnsureBtcFloat(
                    serverInfo.SignerKey.ToXOnlyPubKey(), solverScript, (ulong)SwapSats * 2))
            {
                Assert.Ignore(
                    "the solver has no unencumbered float to fund the lockup with, and topping it " +
                    "up did not land — its balance frees on the operator's settlement schedule.");
            }
        }

        // The solver mints a HOLD invoice, so paying it does not complete here: the HTLC locks in,
        // the solver funds Arkade against it, and the invoice only settles once our claim reveals the
        // preimage. Awaiting the payment before claiming would wait for something only the claim can
        // cause. Firing it and collecting the result after the claim is the corridor's actual shape.
        var payment = Task.Run(() => ctx.Lnd.Pay(pending.Invoice));

        var lockupScript = pending.Contract.GetScriptPubKey().ToHex();
        var lockup = await WaitForVtxo(ctx, lockupScript);
        Assert.That(lockup.Amount, Is.GreaterThanOrEqualTo((ulong)pending.Quote.ToAmount),
            "the solver funded at least what it quoted");

        // The claim reads the lockup out of IVtxoStorage, not off the chain directly. In a running
        // app VtxoSynchronizationService keeps that fed from the intent storage's active scripts;
        // without it here the claim would report the swap unfunded while the VTXO sits on the
        // indexer in plain sight.
        await using var sync = new VtxoSynchronizationService(
            ctx.VtxoStorage, ctx.Transport, [ctx.IntentStorage]);
        await sync.StartAsync(default);

        var stored = await Poll(async () => (await ctx.VtxoStorage.GetVtxos(
            scripts: [lockupScript])).Any(v => !v.IsSpent() && !v.Swept), SolverTimeout);
        Assert.That(stored, Is.True, "the lockup reached the wallet's own view of the chain");

        // Only reachable with the preimage, so a spend here is proof the claim script validated.
        var claimed = await ctx.Intents.ClaimLightningReceiveAsync(pending.RfqId);
        Assert.That(claimed.Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));

        var spent = await Poll(async () => (await GetVtxo(ctx, lockupScript))?.SpentByTransactionId is
            { Length: > 0 }, SolverTimeout);
        Assert.That(spent, Is.True, "the claim landed on the Arkade side");

        // Only now can the payment finish: the preimage our claim published is what releases the
        // hold. A settled invoice here is the proof the two sides of the swap are the same secret.
        Assert.That(await Task.WhenAny(payment, Task.Delay(SolverTimeout)), Is.SameAs(payment),
            "the payer's invoice settled once the claim published the preimage");
        await payment;
    }

    /// <summary>
    /// A swap nobody funds stays unclaimable, and claiming it fails rather than half-completing.
    /// </summary>
    /// <remarks>
    /// The cheap half of the refund story. The other half — <c>refundWithoutReceiver</c> on a send
    /// swap — cannot be driven here: it unlocks at <c>refund_locktime</c>, which the solver sets days
    /// out, and no test can wait that long or move the chain's clock past it. That path stays
    /// covered by the state machine's unit tests until a solver exposes a shorter locktime for
    /// testing.
    /// </remarks>
    [Test]
    public async Task Receive_NotYetFunded_IsNotClaimable()
    {
        var ctx = await SetUpAsync();
        var covclaimdKey = await ctx.ReadCovclaimdPubKeyAsync();

        var pending = await ctx.Intents.ReceiveFromLightningAsync(
            ctx.WalletId, SwapSats, ctx.Rfq, covclaimdKey);

        // The invoice is deliberately left unsettled, so the solver has no reason to fund.
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ctx.Intents.ClaimLightningReceiveAsync(pending.RfqId));

        var stored = await ctx.Intents.GetAsync(pending.RfqId);
        Assert.That(stored!.Status, Is.Not.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
    }

    // ─── Setup + helpers ──────────────────────────────────────────────

    private sealed record Ctx(
        string WalletId,
        IClientTransport Transport,
        ArkadeIntentsService Intents,
        IRfqTransport Rfq,
        LndCliClient Lnd,
        Uri CovclaimdUrl,
        IVtxoStorage VtxoStorage,
        IArkadeIntentStorage IntentStorage)
    {
        /// <summary>
        /// Reads covclaimd's key from the solver's own endpoint rather than hardcoding it.
        /// </summary>
        /// <remarks>
        /// covclaimd generates its key at startup, so a rebuilt stack invalidates any copy. A stale
        /// one seals the preimage to a daemon that cannot open it, which nothing on the wire
        /// notices — the swap simply loses its offline claim path.
        /// </remarks>
        public async Task<string> ReadCovclaimdPubKeyAsync()
        {
            using var http = new HttpClient { BaseAddress = CovclaimdUrl };
            var doc = await http.GetFromJsonAsync<JsonElement>("v1/preimage/covclaimd-pubkey");
            return doc.GetProperty("covclaimd_pub_key").GetString()!;
        }
    }

    private static async Task<Ctx> SetUpAsync()
    {
        var solverUrl = Env("ARKADE_LN_SOLVER_URL");
        if (solverUrl is null)
        {
            Assert.Ignore(
                "needs a Lightning solver started by hand — set ARKADE_LN_SOLVER_URL. " +
                "The solver is not part of the regtest stack.");
        }

        var lnd = new LndCliClient(Env("ARKADE_LND_CONTAINER") ?? "lnd");
        if (!lnd.CanRoute())
        {
            Assert.Ignore(
                "the Lightning node has no active channel — after a stack restart the peers need " +
                "reconnecting before either corridor can move a payment.");
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
            submitHandlers: [new ArkadeEmulatorSpendSubmitter(emulator)]);

        var intentStorage = new InMemoryIntentStorage();

        var send = new LightningSwapClient(
            w.clientTransport, emulator, w.contractService, spendingService,
            intentStorage, w.contracts, w.vtxoStorage, w.walletProvider);

        var receive = new LightningReceiveClient(
            w.clientTransport, emulator, w.contractService, spendingService,
            intentStorage, w.contracts, w.vtxoStorage);

        // The asset corridor is not exercised here, so its client is left out rather than
        // constructed to satisfy a signature.
        var intents = new ArkadeIntentsService(
            null!, send, receive, intentStorage, w.vtxoStorage, TimeProvider.System);

        var solver = new Uri(solverUrl!.EndsWith('/') ? solverUrl : solverUrl + "/");
        var rfq = new HttpRfqTransport(new HttpClient(), solver);
        var covclaimd = new Uri(Env("ARKADE_COVCLAIMD_URL") ?? "http://localhost:7271");

        return new Ctx(w.walletIdentifier, w.clientTransport, intents, rfq, lnd, covclaimd,
            w.vtxoStorage, intentStorage);
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

    private static async Task<bool> Poll(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(1000);
        }
        return false;
    }

    /// <summary>
    /// Just enough of a Lightning node to mint, pay and check an invoice, over <c>docker exec</c>.
    /// </summary>
    /// <remarks>
    /// Shelling into the container instead of using LND's REST API is what keeps this honest: the
    /// regtest stack does not publish that port, and the credentials needed to reach it are
    /// regenerated by every rebuild, so a copy on disk is wrong more often than it is right.
    /// </remarks>
    private sealed class LndCliClient(string container)
    {
        public (string Bolt11, string PaymentHash) CreateInvoice(long sats, string memo)
        {
            var json = Run("addinvoice", "--amt", sats.ToString(), "--memo", memo);
            // lncli already renders r_hash as hex, unlike the REST interface's base64.
            return (json.GetProperty("payment_request").GetString()!,
                    json.GetProperty("r_hash").GetString()!);
        }

        public void Pay(string bolt11)
        {
            var hash = Run("decodepayreq", bolt11).GetProperty("payment_hash").GetString()!;

            // A dropped RPC is not a failed payment, and on a regtest stack that degrades under load
            // the two arrive looking identical. Asking the node what it actually did is the only way
            // to tell them apart — retrying blind would risk paying twice, and failing blind would
            // report a corridor bug for a socket that hiccuped.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // --force skips the interactive confirmation; without it lncli waits on a prompt
                    // that never comes and the test hangs rather than failing.
                    var json = Run("payinvoice", "--force", "--json", bolt11);
                    var status = json.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase)) return;

                    var failure = json.TryGetProperty("failure_reason", out var f) ? f.GetString() : "unknown";
                    throw new InvalidOperationException(
                        $"the node refused the solver's invoice: {status ?? "no status"} ({failure})");
                }
                catch (InvalidOperationException) when (attempt < 3 && !Settled(hash))
                {
                    // The call died without the payment landing. Nothing was spent, so try again.
                }
            }
        }

        /// <summary>Whether this node already paid the invoice, whatever the last call reported.</summary>
        private bool Settled(string paymentHashHex)
        {
            try
            {
                return Run("listpayments").GetProperty("payments").EnumerateArray().Any(p =>
                    string.Equals(p.GetProperty("payment_hash").GetString(), paymentHashHex,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.GetProperty("status").GetString(), "SUCCEEDED",
                        StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                // Unreachable node: report "not settled" so the caller retries rather than assuming
                // a payment it cannot see actually happened.
                return false;
            }
        }

        public bool IsSettled(string paymentHashHex) =>
            Run("lookupinvoice", paymentHashHex).GetProperty("settled").GetBoolean();

        /// <summary>Whether the node has a channel that can actually carry a payment.</summary>
        public bool CanRoute()
        {
            try
            {
                return Run("listchannels").GetProperty("channels").EnumerateArray()
                    .Any(c => c.GetProperty("active").GetBoolean());
            }
            catch (Exception)
            {
                // No node reachable at all reads the same as no usable channel: either way this
                // fixture cannot move a payment, and the caller skips rather than fails.
                return false;
            }
        }

        private JsonElement Run(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "exec", container, "lncli", "--network=regtest" }.Concat(args))
            {
                psi.ArgumentList.Add(a);
            }

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("could not start docker");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"lncli {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
            }
            return JsonSerializer.Deserialize<JsonElement>(stdout);
        }
    }

    /// <summary>In-memory <see cref="IArkadeIntentStorage"/> for the test — the real one is EF Core.</summary>
    private sealed class InMemoryIntentStorage : IArkadeIntentStorage
    {
        private readonly Dictionary<string, ArkadeSwapIntent> _byId = new();

        public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
        public event EventHandler? ActiveScriptsChanged;

        public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
            string? id = null,
            ArkadeSwapIntentStatus? status = null,
            string? swapPkScript = null,
            string[]? walletIds = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<ArkadeSwapIntent> q = _byId.Values;
            if (id is not null) q = q.Where(i => i.Id == id);
            if (status is { } s) q = q.Where(i => i.Status == s);
            if (swapPkScript is { }) q = q.Where(i => i.SwapPkScript == swapPkScript);
            if (walletIds is { Length: > 0 }) q = q.Where(i => walletIds.Contains(i.WalletId));
            return Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(q.ToList());
        }

        public Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default)
        {
            _byId[intent.Id] = intent;
            SwapsChanged?.Invoke(this, intent);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateStatus(
            string swapPkScript,
            ArkadeSwapIntentStatus status,
            string? spentTxid = null,
            CancellationToken cancellationToken = default)
        {
            var intent = _byId.Values.FirstOrDefault(i => i.SwapPkScript == swapPkScript);
            if (intent is null) return Task.FromResult(false);

            intent.Status = status;
            if (spentTxid is { Length: > 0 }) intent.SpentTxid = spentTxid;
            SwapsChanged?.Invoke(this, intent);
            return Task.FromResult(true);
        }
    }
}

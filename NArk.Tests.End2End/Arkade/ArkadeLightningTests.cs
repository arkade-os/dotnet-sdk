using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
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
/// <c>ARKADE_LN_SOLVER_URL</c> at it. The tests also need a Lightning node with a REST interface —
/// the send leg needs an invoice for the solver to pay, and the receive leg needs someone to settle
/// the invoice the solver mints — so <c>ARKADE_LND_REST</c> and <c>ARKADE_LND_MACAROON</c> (hex)
/// must point at one on the same network as the solver. Any of the three missing and the test
/// self-<c>Ignore</c>s, which is also what happens in CI, where no solver runs.
/// </para>
/// <para>
/// A rebuilt regtest stack regenerates its LND certificate and macaroon. A stale macaroon here
/// surfaces as a TLS or permission error that reads like a client bug and is not one — re-export it
/// after every <c>--clean</c>.
/// </para>
/// </remarks>
[TestFixture]
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
        var invoice = await ctx.Lnd.CreateInvoiceAsync(SwapSats, "arkade send e2e");

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

        var settled = await Poll(() => ctx.Lnd.IsSettledAsync(invoice.PaymentHash), SolverTimeout);
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

        await ctx.Lnd.PayAsync(pending.Invoice);

        var lockupScript = pending.Contract.GetScriptPubKey().ToHex();
        var lockup = await WaitForVtxo(ctx, lockupScript);
        Assert.That(lockup.Amount, Is.GreaterThanOrEqualTo((ulong)pending.Quote.ToAmount),
            "the solver funded at least what it quoted");

        // Only reachable with the preimage, so a spend here is proof the claim script validated.
        var claimed = await ctx.Intents.ClaimLightningReceiveAsync(pending.RfqId);
        Assert.That(claimed.Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));

        var spent = await Poll(async () => (await GetVtxo(ctx, lockupScript))?.SpentByTransactionId is
            { Length: > 0 }, SolverTimeout);
        Assert.That(spent, Is.True, "the claim landed on the Arkade side");
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
        LndRestClient Lnd,
        Uri SolverUrl)
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
            using var http = new HttpClient { BaseAddress = SolverUrl };
            var doc = await http.GetFromJsonAsync<JsonElement>("v1/preimage/covclaimd-pubkey");
            return doc.GetProperty("pubkey").GetString()!;
        }
    }

    private static async Task<Ctx> SetUpAsync()
    {
        var solverUrl = Env("ARKADE_LN_SOLVER_URL");
        var lndRest = Env("ARKADE_LND_REST");
        var macaroon = Env("ARKADE_LND_MACAROON");

        if (solverUrl is null || lndRest is null || macaroon is null)
        {
            Assert.Ignore(
                "needs a hand-started Lightning solver and an LND: set ARKADE_LN_SOLVER_URL, " +
                "ARKADE_LND_REST and ARKADE_LND_MACAROON (hex). The solver is not part of the " +
                "regtest stack.");
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

        return new Ctx(w.walletIdentifier, w.clientTransport, intents, rfq,
            new LndRestClient(new Uri(lndRest!), macaroon!), solver);
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
    /// Just enough LND REST to mint, pay and check an invoice.
    /// </summary>
    /// <remarks>
    /// Certificate validation is off because a regtest LND signs its own, and the alternative is
    /// pinning a certificate that every stack rebuild replaces.
    /// </remarks>
    private sealed class LndRestClient(Uri baseAddress, string macaroonHex)
    {
        private readonly HttpClient _http = CreateClient(baseAddress, macaroonHex);

        private static HttpClient CreateClient(Uri baseAddress, string macaroonHex)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            var http = new HttpClient(handler)
            {
                BaseAddress = baseAddress.AbsolutePath.EndsWith('/') ? baseAddress : new Uri(baseAddress + "/"),
                Timeout = TimeSpan.FromMinutes(2),
            };
            http.DefaultRequestHeaders.Add("Grpc-Metadata-macaroon", macaroonHex);
            return http;
        }

        public async Task<(string Bolt11, string PaymentHash)> CreateInvoiceAsync(long sats, string memo)
        {
            var response = await PostAsync("v1/invoices", new { value = sats.ToString(), memo });
            // LND returns the hash base64 over REST; the rest of the SDK speaks hex.
            var hash = Convert.ToHexString(
                Convert.FromBase64String(response.GetProperty("r_hash").GetString()!)).ToLowerInvariant();
            return (response.GetProperty("payment_request").GetString()!, hash);
        }

        public async Task PayAsync(string bolt11)
        {
            var response = await PostAsync("v1/channels/transactions", new { payment_request = bolt11 });
            var error = response.TryGetProperty("payment_error", out var e) ? e.GetString() : null;
            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException($"LND could not pay the solver's invoice: {error}");
            }
        }

        public async Task<bool> IsSettledAsync(string paymentHashHex)
        {
            using var response = await _http.GetAsync($"v1/invoice/{paymentHashHex}");
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
            return doc.GetProperty("settled").GetBoolean();
        }

        private async Task<JsonElement> PostAsync(string path, object body)
        {
            using var response = await _http.PostAsJsonAsync(path, body);
            var payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"LND {path} returned {(int)response.StatusCode}: {payload}");
            }
            return JsonSerializer.Deserialize<JsonElement>(payload);
        }
    }

    /// <summary>In-memory <see cref="IArkadeIntentStorage"/> for the test — the real one is EF Core.</summary>
    private sealed class InMemoryIntentStorage : IArkadeIntentStorage
    {
        private readonly Dictionary<string, ArkadeSwapIntent> _byId = new();

        public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
        public event EventHandler? ActiveScriptsChanged;

        public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(
            ArkadeSwapIntentStatus? status = null,
            string? swapPkScript = null,
            string[]? walletIds = null,
            int? skip = null,
            int? take = null,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<ArkadeSwapIntent> q = _byId.Values;
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

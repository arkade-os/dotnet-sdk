using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport;
using NArk.Arkade.Covclaim;
using NArk.Blockchain;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Swaps;

/// <summary>
/// End-to-end coverage for non-interactive claims: a reverse swap whose VHTLC carries
/// a covenant claim leaf is swept by <c>covclaimd</c> without this wallet doing
/// anything.
/// </summary>
/// <remarks>
/// <para>
/// The test deliberately runs <em>no</em> sweeper and no spending service. That is the
/// whole point — if the wallet's own claim path were active it would race the daemon
/// and a pass would not tell us whether the daemon works. Here the wallet is, in
/// effect, offline, so the funds can only move if the covenant path does its job.
/// </para>
/// <para>
/// Requires the regtest stack with the <c>covclaimd</c> profile and a Boltz build that
/// supports non-interactive claims:
/// </para>
/// <code>
/// BOLTZ_IMAGE=altaf4n/boltz:fulmine-v4-support \
/// COVCLAIMD_IMAGE=ghcr.io/arkade-os/covclaimd:v0.0.1-rc.2 \
/// AUTOMINE_INTERVAL=0 ARKD_VTXO_TREE_EXPIRY=511 \
///   node regtest/regtest.mjs start --profile boltz,covclaimd
/// </code>
/// <para>
/// The last two variables are not optional. arkd reads its locktimes by magnitude
/// (below the BIP68 boundary of 512 they are <em>blocks</em>), and the regtest default
/// of 180 blocks is less than what a swap e2e run mines on its own — so VTXOs expire
/// mid-run and funding starts failing with "missing vtxos". 511 keeps the block
/// scheduler while leaving headroom; disabling the auto-miner keeps the tip from
/// drifting between runs.
/// </para>
/// <para>
/// Both the Boltz and ts-sdk changes were unmerged when this was written, so the
/// images above are pinned previews rather than releases. <c>covclaimd</c> is also
/// opt-in: <c>regtest.mjs</c> silently skips the profile when
/// <c>COVCLAIMD_IMAGE</c> is empty.
/// </para>
/// </remarks>
[Category("Swaps")]
[Category("Covclaim")]
public class CovenantClaimSwapTests
{
    private static readonly Uri CovclaimdEndpoint = new("http://localhost:7271");

    private static CovclaimdClient CreateCovclaimdClient() =>
        new(new HttpClient(),
            new OptionsWrapper<CovclaimdOptions>(new CovclaimdOptions { BaseAddress = CovclaimdEndpoint }));

    [Test]
    public async Task CovclaimdClaimsReverseSwapWhileWalletStaysOffline()
    {
        var prereq = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);

        var boltzOptions = new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
        {
            BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
            WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString(),
        });
        var boltzClient = new BoltzClient(new HttpClient(), boltzOptions);

        var covenantClaimProvider = new CovclaimdCovenantClaimProvider(CreateCovclaimdClient());

        var boltzProvider = new BoltzSwapProvider(
            boltzClient,
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), boltzOptions)),
            prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider, swapStorage,
            prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage,
            chainTimeProvider,
            covenantClaimProvider: covenantClaimProvider);

        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService: null!,
            prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
            swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService,
            intentStorage, chainTimeProvider,
            // Registration failures are deliberately non-fatal in production, so without
            // a logger a broken reveal would look identical to a daemon that simply
            // never acted. Surface it.
            logger: new TestOutputLogger<SwapsManagementService>(),
            covenantClaimProvider: covenantClaimProvider);

        await swapMgr.StartAsync(CancellationToken.None);

        var invoice = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateReverseSwap(
                prereq.walletIdentifier,
                new CreateInvoiceParams(LightMoney.Satoshis(50000), "covclaim e2e", TimeSpan.FromHours(1)),
                CancellationToken.None));

        var swap = (await swapStorage.GetSwaps(walletIds: [prereq.walletIdentifier]))
            .OrderByDescending(s => s.CreatedAt)
            .First();

        // The contract we stored must be the covenant variant, otherwise the daemon has
        // nothing it can spend and the rest of the test would be meaningless.
        var storedContract = (await prereq.contracts.GetContracts(
            walletIds: [prereq.walletIdentifier], scripts: [swap.ContractScript])).Single();
        var contract = (VHTLCContract)ArkContractParser.Parse(
            storedContract.Type, storedContract.AdditionalData, Network.RegTest)!;

        Assert.Multiple(() =>
        {
            Assert.That(contract.CovenantClaimKey, Is.Not.Null,
                "swap contract should carry a covenant claim key");
            Assert.That(contract.GetTapScriptList(), Has.Length.EqualTo(7),
                "covenant swap should have the extra leaf");
            Assert.That(contract.GetArkAddress().ToString(false), Is.EqualTo(swap.Address),
                "our contract must reproduce the address Boltz funds");
        });

        var claimDestination = contract.CreateCovenantClaimScript();
        Assert.That(claimDestination, Is.Not.Null);

        // Funding the lockup is what wakes covclaimd up.
        await DockerHelper.PayLndInvoice(invoice);

        // From here the wallet does nothing: no sweeper, no spending service, no sync.
        // Queried straight off the indexer rather than this wallet's VTXO storage —
        // nothing is populating that storage precisely because the wallet is idle, so
        // polling it would report "no claim" no matter what actually happened.
        var claimed = await WaitForClaimAsync(
            prereq.clientTransport, swap.ContractScript, TimeSpan.FromMinutes(3));

        Assert.That(claimed, Is.True,
            "covclaimd should have swept the VHTLC while the wallet stayed offline");
    }

    /// <summary>
    /// The other direction Boltz sends Arkade on. Worth covering separately because the
    /// VHTLC is reconstructed from different response fields — the sender key comes from
    /// <c>claimDetails.serverPublicKey</c> rather than a refund key — so the address
    /// check exercises a code path the reverse-swap test never touches.
    /// </summary>
    /// <remarks>
    /// Stops at the address/leaf assertions rather than funding the BTC side: driving an
    /// on-chain lockup adds a confirmation wait without testing anything new about the
    /// covenant path, which the reverse-swap test already takes all the way to a claim.
    /// </remarks>
    [Test]
    public async Task BtcToArkChainSwapBuildsCovenantClaimContract()
    {
        var prereq = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        var intentStorage = TestStorage.CreateIntentStorage();
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);

        var boltzOptions = new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
        {
            BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
            WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString(),
        });
        var covenantClaimProvider = new CovclaimdCovenantClaimProvider(CreateCovclaimdClient());

        var boltzProvider = new BoltzSwapProvider(
            new BoltzClient(new HttpClient(), boltzOptions),
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(), boltzOptions)),
            prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider, swapStorage,
            prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage,
            chainTimeProvider,
            covenantClaimProvider: covenantClaimProvider);

        await using var swapMgr = new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService: null!,
            prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
            swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService,
            intentStorage, chainTimeProvider,
            logger: new TestOutputLogger<SwapsManagementService>(),
            covenantClaimProvider: covenantClaimProvider);

        await swapMgr.StartAsync(CancellationToken.None);

        var (_, swapId, _) = await swapMgr.InitiateBtcToArkChainSwap(
            prereq.walletIdentifier, 100_000, CancellationToken.None);

        var swap = (await swapStorage.GetSwaps(
            walletIds: [prereq.walletIdentifier], swapIds: [swapId])).Single();

        var storedContract = (await prereq.contracts.GetContracts(
            walletIds: [prereq.walletIdentifier], scripts: [swap.ContractScript])).Single();
        var contract = (VHTLCContract)ArkContractParser.Parse(
            storedContract.Type, storedContract.AdditionalData, Network.RegTest)!;

        Assert.Multiple(() =>
        {
            Assert.That(contract.CovenantClaimKey, Is.Not.Null,
                "chain swap contract should carry a covenant claim key");
            Assert.That(contract.GetTapScriptList(), Has.Length.EqualTo(7),
                "covenant chain swap should have the extra leaf");
        });

        // CreateBtcToArkSwapAsync throws on a mismatch, so reaching here already proves
        // our seven-leaf reconstruction equals the address Boltz published. Asserted
        // explicitly so a future refactor that drops that check still fails loudly.
        Assert.That(contract.GetArkAddress().ScriptPubKey.ToHex(), Is.EqualTo(swap.ContractScript));
    }

    /// <summary>Minimal logger that routes straight to the NUnit test output.</summary>
    private sealed class TestOutputLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            TestContext.Out.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            if (exception is not null)
                TestContext.Out.WriteLine(exception.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Polls the indexer until the swap's VTXO shows up spent — the observable signal
    /// that something claimed it. Returns false on timeout rather than throwing so the
    /// caller can attach a message explaining what the timeout means.
    /// </summary>
    private static async Task<bool> WaitForClaimAsync(
        IClientTransport transport, string contractScript, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await foreach (var vtxo in transport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { contractScript }))
            {
                if (vtxo.IsSpent())
                    return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return false;
    }
}

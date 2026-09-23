using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;
using NArk.Storage.EfCore.Storage;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Wallet;

/// <summary>
/// Covers the descriptor-recycling branch of
/// <see cref="HierarchicalDeterministicAddressProvider"/>'s SendToSelf purpose, which reuses an
/// input's descriptor for the output. Recycling deactivates the reused script, so these tests pin
/// down which inputs may be recycled: a spent-and-done change script may, a script the wallet is
/// still advertising as an inbound destination may not.
/// </summary>
[TestFixture]
public class HierarchicalDeterministicAddressProviderRecycleTests
{
    private const string WalletId = "w1";

    private const string AccountDescriptorTemplate =
        "tr([73c5da0a/86'/1'/0']tpubDDpWvmUrPZrhSPmUzCMBHffvC3HyMAPnWDSAQNBTnj1iZeJa7BZQEttFiP4DS4GCcXQHezdXhn86Hj6LHX5EDstXPWrMaSneRWM8yUf6NFd/*)";

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly ArkServerInfo TestServerInfo = new(
        Dust: Money.Satoshis(330),
        SignerKey: TestServerKey,
        DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
        Network: Network.RegTest,
        UnilateralExit: new Sequence(144),
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ForfeitPubKey: TestServerKey.Extract().XOnlyPubKey,
        CheckpointTapScript: new UnilateralPathArkTapScript(
            new Sequence(144),
            new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>())),
        FeeTerms: new ArkOperatorFeeTerms("0", "0", "0", "0", "0"),
        Digest: "");

    private SqliteConnection _connection = null!;
    private DbContextOptions<TestArkDbContext> _options = null!;
    private EfCoreContractStorage _contractStorage = null!;
    private IWalletStorage _walletStorage = null!;
    private IClientTransport _transport = null!;
    private ContractService _contractService = null!;
    private ArkWalletInfo _wallet = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<TestArkDbContext>().UseSqlite(_connection).Options;

        using (var ctx = new TestArkDbContext(_options))
        {
            ctx.Database.EnsureCreated();
            ctx.Set<ArkWalletEntity>().Add(new ArkWalletEntity { Id = WalletId });
            ctx.SaveChanges();
        }

        _contractStorage = new EfCoreContractStorage(
            new TestArkDbContextFactory(_options), new ArkStorageOptions());

        _wallet = new ArkWalletInfo(
            WalletId, null, null, WalletType.HD, AccountDescriptorTemplate, LastUsedIndex: 0);

        _transport = Substitute.For<IClientTransport>();
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TestServerInfo));

        // Wallet storage backed by the local _wallet record so index hand-out advances for real.
        _walletStorage = Substitute.For<IWalletStorage>();
        _walletStorage.GetWalletById(WalletId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ArkWalletInfo?>(_wallet));
        _walletStorage.UpdateLastUsedIndex(WalletId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _wallet = _wallet with { LastUsedIndex = call.ArgAt<int>(1) };
                return Task.CompletedTask;
            });

        var addressProvider = new HierarchicalDeterministicAddressProvider(
            _transport,
            new AsyncSafetyService(),
            _walletStorage,
            _contractStorage,
            _wallet,
            Network.RegTest,
            sweepDestination: null);

        var walletProvider = Substitute.For<IWalletProvider>();
        walletProvider.GetAddressProviderAsync(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeAddressProvider?>(addressProvider));

        _contractService = new ContractService(walletProvider, _contractStorage, _transport);
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    /// <summary>
    /// A receive address the wallet handed out stays Active — and therefore stays in the scan set —
    /// even after a renewal that consumes the coin sitting on it. Renewal reaches this through
    /// <see cref="SimpleIntentScheduler"/>, which derives its output as SendToSelf and passes the
    /// coins' own contracts as inputs; recycling the published descriptor there would deactivate a
    /// script a payer may still send to, and the payment would never be seen.
    /// </summary>
    [Test]
    public async Task Renewal_DoesNotDropPublishedReceiveAddress_FromActiveScripts()
    {
        var receive = await _contractService.DeriveContract(
            WalletId, NextContractPurpose.Receive, ContractActivityState.Active);
        var receiveScript = receive.GetScriptPubKey().ToHex();

        Assert.That(await ActiveScriptsAsync(), Does.Contain(receiveScript),
            "a freshly derived receive address must be scanned");

        // Exactly what SimpleIntentScheduler does per chunk: SendToSelf output, coins' contracts as inputs.
        await _contractService.DeriveContract(
            WalletId, NextContractPurpose.SendToSelf, [receive], ContractActivityState.Inactive);

        Assert.That(await ActiveScriptsAsync(), Does.Contain(receiveScript),
            "renewal must not drop a published receive address out of the scan set");
    }

    /// <summary>
    /// The other half of the same rule: recycling is still the right call for an internal change
    /// script, whose coin is being consumed and which was never advertised. Guards against a fix
    /// that simply switches recycling off.
    /// </summary>
    [Test]
    public async Task Renewal_StillRecyclesSpentChangeAddress()
    {
        // A change output from an earlier send. SendToSelf picks its own state, so this lands as
        // AwaitingFundsBeforeDeactivate — watched only until the coin arrives, never advertised.
        var change = await _contractService.DeriveContract(WalletId, NextContractPurpose.SendToSelf);
        var changeScript = change.GetScriptPubKey().ToHex();

        var renewalOutput = await _contractService.DeriveContract(
            WalletId, NextContractPurpose.SendToSelf, [change], ContractActivityState.Inactive);

        Assert.That(renewalOutput.GetScriptPubKey().ToHex(), Is.EqualTo(changeScript),
            "a spent change descriptor should still be recycled rather than burning a fresh index");
        Assert.That(await ActiveScriptsAsync(), Does.Not.Contain(changeScript),
            "a recycled change script stops being scanned");
    }

    /// <summary>
    /// The pre-existing carve-out for invoice scripts still holds, and holds independently of the
    /// activity state: an invoice contract is watched as AwaitingFundsBeforeDeactivate, so the
    /// Active check above does not cover it.
    /// </summary>
    [Test]
    public async Task Renewal_DoesNotRecycleInvoiceScript()
    {
        var invoice = await _contractService.DeriveContract(
            WalletId,
            NextContractPurpose.Receive,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = "invoice:lnbc1" });
        var invoiceScript = invoice.GetScriptPubKey().ToHex();

        var renewalOutput = await _contractService.DeriveContract(
            WalletId, NextContractPurpose.SendToSelf, [invoice], ContractActivityState.Inactive);

        Assert.That(renewalOutput.GetScriptPubKey().ToHex(), Is.Not.EqualTo(invoiceScript),
            "an invoice descriptor must not be recycled");
        Assert.That(await ActiveScriptsAsync(), Does.Contain(invoiceScript),
            "renewal must not drop an invoice script out of the scan set");
    }

    private async Task<HashSet<string>> ActiveScriptsAsync() =>
        await ((IActiveScriptsProvider)_contractStorage).GetActiveScripts();

    // Ticks mapping is the documented opt-in that lets GetContracts' ORDER BY CreatedAt
    // translate on SQLite (see EfCoreSqliteOrderByTests).
    private class TestArkDbContext(DbContextOptions<TestArkDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }

    private class TestArkDbContextFactory(DbContextOptions<TestArkDbContext> options) : IArkDbContextFactory
    {
        public Task<DbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<DbContext>(new TestArkDbContext(options));
    }
}

using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Recovery;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Recovery;

[TestFixture]
public class HdWalletRecoveryServiceTests
{
    private const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private const string AccountDescriptorTemplate =
        "tr([73c5da0a/86'/1'/0']tpubDDpWvmUrPZrhSPmUzCMBHffvC3HyMAPnWDSAQNBTnj1iZeJa7BZQEttFiP4DS4GCcXQHezdXhn86Hj6LHX5EDstXPWrMaSneRWM8yUf6NFd/*)";

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly ArkServerInfo TestServerInfo = new(
        Dust: Money.Satoshis(330),
        SignerKey: TestServerKey,
        DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
        Network: Network.RegTest,
        UnilateralExit: new Sequence(144),
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ForfeitPubKey: TestServerKey.Extract().XOnlyPubKey,
        CheckpointTapScript: new UnilateralPathArkTapScript(
            new Sequence(144),
            new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>())),
        FeeTerms: new ArkOperatorFeeTerms("0", "0", "0", "0", "0"));

    private IWalletStorage _walletStorage = null!;
    private IContractStorage _contractStorage = null!;
    private IClientTransport _transport = null!;
    private List<ArkContractEntity> _persistedContracts = null!;
    private int _lastUsedIndexAfter = -1;

    [SetUp]
    public void SetUp()
    {
        _walletStorage = Substitute.For<IWalletStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _transport = Substitute.For<IClientTransport>();
        _persistedContracts = new List<ArkContractEntity>();
        _lastUsedIndexAfter = -1;

        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TestServerInfo));

        _contractStorage.SaveContract(Arg.Any<ArkContractEntity>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                _persistedContracts.Add(callInfo.Arg<ArkContractEntity>());
                return Task.CompletedTask;
            });

        _walletStorage.UpdateLastUsedIndex(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                _lastUsedIndexAfter = callInfo.ArgAt<int>(1);
                return Task.CompletedTask;
            });
    }

    [Test]
    public async Task Scan_NoUsage_StopsAtGap_NoContractsSaved()
    {
        SetupWallet();
        var provider = new StubProvider("indexer", _ => false);
        var sut = NewService([provider]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 5));

        Assert.That(report.HighestUsedIndex, Is.EqualTo(-1));
        Assert.That(report.ScannedCount, Is.EqualTo(5));
        Assert.That(report.DiscoveredContracts, Is.Empty);
        Assert.That(_persistedContracts, Is.Empty);
        Assert.That(_lastUsedIndexAfter, Is.EqualTo(-1));
    }

    [Test]
    public async Task Scan_UsageAtIndexZero_ThenGap_StopsAtGap()
    {
        SetupWallet();
        var provider = new StubProvider("indexer", i => i == 0);
        var sut = NewService([provider]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 5));

        Assert.That(report.HighestUsedIndex, Is.EqualTo(0));
        Assert.That(report.ScannedCount, Is.EqualTo(6)); // index 0 + 5 misses
        Assert.That(report.DiscoveredContracts, Has.Count.EqualTo(1));
        Assert.That(_persistedContracts, Has.Count.EqualTo(1));
        Assert.That(_lastUsedIndexAfter, Is.EqualTo(1));
    }

    [Test]
    public async Task Scan_InterleavedUsage_FindsHighestAndAllContracts()
    {
        SetupWallet();
        var hits = new HashSet<int> { 0, 3, 7 };
        var provider = new StubProvider("indexer", hits.Contains);
        var sut = NewService([provider]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 4));

        Assert.That(report.HighestUsedIndex, Is.EqualTo(7));
        Assert.That(report.DiscoveredContracts.Select(d => d.Index),
            Is.EquivalentTo(hits));
        Assert.That(_persistedContracts, Has.Count.EqualTo(3));
        Assert.That(_lastUsedIndexAfter, Is.EqualTo(8));
    }

    [Test]
    public async Task Scan_TwoProviders_OrSemantics()
    {
        SetupWallet();
        var indexer = new StubProvider("indexer", i => i == 2);
        var boltz = new StubProvider("boltz", i => i == 5, returnContracts: false);
        var sut = NewService([indexer, boltz]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 4));

        Assert.That(report.HighestUsedIndex, Is.EqualTo(5));
        Assert.That(report.ProviderHits["indexer"], Is.EqualTo(1));
        Assert.That(report.ProviderHits["boltz"], Is.EqualTo(1));
        // Only "indexer" returned contracts; "boltz" returned (used:true, contracts:[])
        Assert.That(report.DiscoveredContracts, Has.Count.EqualTo(1));
        Assert.That(_persistedContracts, Has.Count.EqualTo(1));
        Assert.That(_lastUsedIndexAfter, Is.EqualTo(6));
    }

    [Test]
    public async Task Scan_ProviderThrows_TreatedAsNotFound()
    {
        SetupWallet();
        var throwingProvider = new StubProvider("flaky", _ => throw new InvalidOperationException("boom"));
        var goodProvider = new StubProvider("indexer", i => i == 1);
        var sut = NewService([throwingProvider, goodProvider]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 3));

        Assert.That(report.HighestUsedIndex, Is.EqualTo(1));
        Assert.That(report.ProviderHits["flaky"], Is.EqualTo(0));
        Assert.That(report.ProviderHits["indexer"], Is.EqualTo(1));
    }

    [Test]
    public async Task Scan_DuplicateContractsAcrossProviders_DedupedByScript()
    {
        SetupWallet();
        // Both providers return a contract that produces the same script for index 0
        var p1 = new StubProvider("indexer", i => i == 0);
        var p2 = new StubProvider("boarding", i => i == 0);
        var sut = NewService([p1, p2]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 2));

        Assert.That(report.HighestUsedIndex, Is.EqualTo(0));
        // Both providers returned a contract, but the orchestrator dedups on script
        Assert.That(_persistedContracts, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Scan_StartIndex_BeginsThere()
    {
        SetupWallet();
        var provider = new StubProvider("indexer", _ => false);
        var sut = NewService([provider]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 3, StartIndex: 50));

        Assert.That(report.ScannedCount, Is.EqualTo(3));
        // The provider should have been queried with indices 50, 51, 52 — verified via call count.
        Assert.That(provider.IndicesProbed, Is.EquivalentTo(new[] { 50, 51, 52 }));
    }

    [Test]
    public async Task Scan_MaxIndex_StopsThereEvenWithoutGap()
    {
        SetupWallet();
        var provider = new StubProvider("indexer", _ => true); // every index is a hit, never gaps
        var sut = NewService([provider]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 100, MaxIndex: 4));

        Assert.That(report.ScannedCount, Is.EqualTo(5)); // indices 0..4
        Assert.That(report.HighestUsedIndex, Is.EqualTo(4));
    }

    [Test]
    public void Scan_SingleKeyWallet_Throws()
    {
        var wallet = new ArkWalletInfo("w1", "secret", null, WalletType.SingleKey, null, 0);
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        var sut = NewService([new StubProvider("indexer", _ => true)]);

        Assert.ThrowsAsync<InvalidOperationException>(() => sut.ScanAsync("w1"));
    }

    [Test]
    public void Scan_UnknownWallet_Throws()
    {
        _walletStorage.GetWalletById("missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(null));

        var sut = NewService([new StubProvider("indexer", _ => true)]);

        Assert.ThrowsAsync<InvalidOperationException>(() => sut.ScanAsync("missing"));
    }

    [Test]
    public async Task Scan_NullProvidersFiltered()
    {
        SetupWallet();
        var sut = NewService([NullContractDiscoveryProvider.Instance]);

        var report = await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 3));

        Assert.That(report.ProviderHits, Does.Not.ContainKey("null"));
        Assert.That(report.ScannedCount, Is.EqualTo(3));
    }

    [Test]
    public async Task Scan_DoesNotLowerLastUsedIndex()
    {
        SetupWallet(lastUsedIndex: 50);
        var provider = new StubProvider("indexer", i => i == 5);
        var sut = NewService([provider]);

        await sut.ScanAsync("w1", new RecoveryOptions(GapLimit: 3));

        // Wallet already had LastUsedIndex=50; we must never lower it.
        // The orchestrator should NOT call UpdateLastUsedIndex when its
        // computed value is below the stored one.
        Assert.That(_lastUsedIndexAfter, Is.EqualTo(-1));
    }

    private void SetupWallet(int lastUsedIndex = 0)
    {
        var wallet = new ArkWalletInfo("w1", Mnemonic, null, WalletType.HD, AccountDescriptorTemplate, lastUsedIndex);
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));
    }

    private HdWalletRecoveryService NewService(IEnumerable<IContractDiscoveryProvider> providers)
        => new(providers, _walletStorage, _contractStorage, _transport);

    /// <summary>
    /// Test-side discovery provider: a hit predicate decides whether any given
    /// index counts as used. When <paramref name="returnContracts"/> is true
    /// (default) it returns a single ArkPaymentContract built from the
    /// derived descriptor — gives the orchestrator something to persist.
    /// </summary>
    private sealed class StubProvider(
        string name,
        Func<int, bool> hits,
        bool returnContracts = true) : IContractDiscoveryProvider
    {
        public List<int> IndicesProbed { get; } = [];
        public string Name => name;

        public Task<DiscoveryResult> DiscoverAsync(
            ArkWalletInfo wallet,
            OutputDescriptor userDescriptor,
            int index,
            CancellationToken cancellationToken = default)
        {
            IndicesProbed.Add(index);
            if (!hits(index))
                return Task.FromResult(DiscoveryResult.NotFound);

            if (!returnContracts)
                return Task.FromResult(new DiscoveryResult(true, []));

            var contract = new ArkPaymentContract(
                TestServerInfo.SignerKey,
                TestServerInfo.UnilateralExit,
                userDescriptor);
            return Task.FromResult(new DiscoveryResult(true, [contract]));
        }
    }
}

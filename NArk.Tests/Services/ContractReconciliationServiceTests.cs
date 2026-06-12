using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Recovery;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.Services;

[TestFixture]
public class ContractReconciliationServiceTests
{
    private IWalletStorage _walletStorage = null!;
    private IContractStorage _contractStorage = null!;
    private ISingleKeyDefaultEnsurer _ensurer = null!;
    private IServerInfoCacheInvalidation _serverInfoCache = null!;

    private const string CurrentScript = "aa" + "00";
    private const string StaleScript = "bb" + "11";

    [SetUp]
    public void SetUp()
    {
        _walletStorage = Substitute.For<IWalletStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _ensurer = Substitute.For<ISingleKeyDefaultEnsurer>();
        _serverInfoCache = Substitute.For<IServerInfoCacheInvalidation>();

        // Ensurer reports the current-signer default script.
        _ensurer.EnsureDefaultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CurrentScript));

        // Default: no active contracts.
        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Any<bool?>(),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([]));
    }

    private ContractReconciliationService CreateService() =>
        new(_walletStorage, _contractStorage, _ensurer, _serverInfoCache);

    private static ArkWalletInfo SingleKeyWallet(string id = "w1") =>
        new(id, null, null, WalletType.SingleKey, "tr(0000000000000000000000000000000000000000000000000000000000000001)", 0);

    private static ArkWalletInfo HdWallet(string id = "w2") =>
        new(id, null, null, WalletType.HD, "tr([00000000/86'/1'/0']xpub.../0/*)", 0);

    private static ArkContractEntity ContractEntity(string script, string? source)
    {
        return new ArkContractEntity(
            Script: script,
            ActivityState: ContractActivityState.Active,
            Type: "payment",
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: "w1",
            CreatedAt: DateTimeOffset.UtcNow)
        {
            Metadata = source is null ? null : new Dictionary<string, string> { ["Source"] = source },
        };
    }

    private void SetupActiveContracts(string walletId, params ArkContractEntity[] contracts)
    {
        _contractStorage.GetContracts(
                walletIds: Arg.Is<string[]?>(w => w != null && w.Contains(walletId)),
                scripts: Arg.Any<string[]?>(),
                isActive: true,
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>(contracts));
    }

    [Test]
    public async Task ReconcileWalletAsync_SingleKey_ensures_default_and_supersedes_stale_default_only()
    {
        var wallet = SingleKeyWallet();
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        var staleDefault = ContractEntity(StaleScript, source: "Default");   // pre-rotation default → should be deactivated
        var currentDefault = ContractEntity(CurrentScript, source: "Default"); // current default → must NOT be deactivated
        var nonDefault = ContractEntity("cc22", source: "recovery:singlekey"); // not a Default → must NOT be deactivated
        SetupActiveContracts("w1", staleDefault, currentDefault, nonDefault);

        var sut = CreateService();
        await sut.ReconcileWalletAsync("w1", CancellationToken.None);

        // Ensured the current-signer default.
        await _ensurer.Received(1).EnsureDefaultAsync("w1", Arg.Any<CancellationToken>());

        // Deactivated the stale Source=Default whose script differs.
        await _contractStorage.Received(1).UpdateContractActivityState(
            "w1", StaleScript, ContractActivityState.Inactive, Arg.Any<CancellationToken>());

        // Did NOT deactivate the current default (matching script).
        await _contractStorage.DidNotReceive().UpdateContractActivityState(
            "w1", CurrentScript, Arg.Any<ContractActivityState>(), Arg.Any<CancellationToken>());

        // Did NOT deactivate the non-Default contract.
        await _contractStorage.DidNotReceive().UpdateContractActivityState(
            "w1", "cc22", Arg.Any<ContractActivityState>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReconcileWalletAsync_NonSingleKey_is_noop()
    {
        var wallet = HdWallet();
        _walletStorage.GetWalletById("w2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        var sut = CreateService();
        await sut.ReconcileWalletAsync("w2", CancellationToken.None);

        await _ensurer.DidNotReceive().EnsureDefaultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _contractStorage.DidNotReceive().UpdateContractActivityState(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ContractActivityState>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReconcileWalletAsync_MissingWallet_is_noop()
    {
        _walletStorage.GetWalletById("nope", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(null));

        var sut = CreateService();
        await sut.ReconcileWalletAsync("nope", CancellationToken.None);

        await _ensurer.DidNotReceive().EnsureDefaultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _contractStorage.DidNotReceive().UpdateContractActivityState(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ContractActivityState>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WalletSaved_SingleKey_triggers_ensure()
    {
        var wallet = SingleKeyWallet();
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));
        // Startup pass enumerates wallets — keep it empty so only the event drives the ensure.
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>()));

        await using var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        _walletStorage.WalletSaved += Raise.Event<EventHandler<ArkWalletInfo>>(_walletStorage, wallet);

        await WaitForAsync(() => _ensurer.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(ISingleKeyDefaultEnsurer.EnsureDefaultAsync)));

        await _ensurer.Received().EnsureDefaultAsync("w1", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WalletSaved_Hd_does_not_ensure()
    {
        var hd = HdWallet();
        _walletStorage.GetWalletById("w2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(hd));
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>()));

        await using var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        _walletStorage.WalletSaved += Raise.Event<EventHandler<ArkWalletInfo>>(_walletStorage, hd);

        // Give the worker a chance to (not) process.
        await Task.Delay(200);

        await _ensurer.DidNotReceive().EnsureDefaultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
    }
}

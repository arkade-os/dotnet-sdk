using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Recovery;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Services;

[TestFixture]
public class ContractReconciliationServiceTests
{
    private IWalletStorage _walletStorage = null!;
    private IContractStorage _contractStorage = null!;
    private ISingleKeyDefaultEnsurer _ensurer = null!;
    private IServerInfoCacheInvalidation _serverInfoCache = null!;
    private IClientTransport _clientTransport = null!;

    private const string CurrentScript = "aa" + "00";
    private const string StaleScript = "bb" + "11";

    [SetUp]
    public void SetUp()
    {
        _walletStorage = Substitute.For<IWalletStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _ensurer = Substitute.For<ISingleKeyDefaultEnsurer>();
        _serverInfoCache = Substitute.For<IServerInfoCacheInvalidation>();
        _clientTransport = Substitute.For<IClientTransport>();

        // Backend reachable by default: the reconcile-all availability probe succeeds.
        // The success value is never inspected (it only gates whether the pass proceeds),
        // so a null ArkServerInfo is fine here.
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkServerInfo>(null!));

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

    private ContractReconciliationService CreateService(TimeSpan? retryDelay = null) =>
        new(_walletStorage, _contractStorage, _ensurer, _serverInfoCache, _clientTransport, logger: null,
            reconcileAllRetryDelay: retryDelay);

    private static ArkWalletInfo SingleKeyWallet(string id = "w1") =>
        new(id, null, null, WalletType.SingleKey, "tr(0000000000000000000000000000000000000000000000000000000000000001)", 0);

    private static ArkWalletInfo SingleKeyWalletWithDestination(string id = "w1") =>
        new(id, null, "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", WalletType.SingleKey,
            "tr(0000000000000000000000000000000000000000000000000000000000000001)", 0);

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
    public async Task ReconcileWalletAsync_WithSweepDestination_doesNotDeactivate_matchingPaymentDefault()
    {
        // C1 guard: even when a sweep Destination is configured, the ensurer returns the
        // payment-contract script (built directly). The active Source="Default" row whose
        // script matches must NOT be superseded — the reconciler must not deactivate the
        // genuine payment default for a destination-configured wallet.
        var wallet = SingleKeyWalletWithDestination();
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        var paymentDefault = ContractEntity(CurrentScript, source: "Default");
        SetupActiveContracts("w1", paymentDefault);

        var sut = CreateService();
        await sut.ReconcileWalletAsync("w1", CancellationToken.None);

        await _ensurer.Received(1).EnsureDefaultAsync("w1", Arg.Any<CancellationToken>());
        await _contractStorage.DidNotReceive().UpdateContractActivityState(
            "w1", CurrentScript, Arg.Any<ContractActivityState>(), Arg.Any<CancellationToken>());
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

    [Test]
    public async Task WalletSaved_Hd_with_stale_destination_enqueues_and_flags()
    {
        // OnWalletSaved is broadened to enqueue HD wallets that carry a destination (so a re-save
        // clears the flag). Here an HD wallet with a stale destination must be reconciled and flagged
        // — but still NOT get a default ensured (that path stays SingleKey-only).
        var deprecated = NewKey();
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeServerInfo(currentSigner: NewKey(), deprecated: [deprecated])));

        var hd = HdWallet() with { Destination = MakeAddress(serverKey: deprecated).ToString(isMainnet: false) };
        _walletStorage.GetWalletById(hd.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(hd));
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>()));

        await using var sut = CreateService();
        await sut.StartAsync(CancellationToken.None);

        _walletStorage.WalletSaved += Raise.Event<EventHandler<ArkWalletInfo>>(_walletStorage, hd);

        await WaitForAsync(() => _walletStorage.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IWalletStorage.SetMetadataValue)));

        await _walletStorage.Received().SetMetadataValue(
            hd.Id, DestinationSafety.PendingConfirmationMetadataKey,
            Arg.Is<string?>(v => v != null), Arg.Any<CancellationToken>());
        await _ensurer.DidNotReceive().EnsureDefaultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartupPass_BackendUnavailable_isRetried_thenReconciles()
    {
        // I1 follow-up: "arkd down at boot" does NOT surface as LoadAllWallets throwing
        // (that's pure DB) — it surfaces as the per-wallet EnsureDefaultAsync transport call
        // throwing, which ReconcileAllAsync would otherwise absorb, so the pass would silently
        // reconcile nothing and never retry. The reconcile-all pass must probe backend
        // availability up front (GetServerInfoAsync); when the backend is unreachable the WHOLE
        // pass fails and is requeued (bounded). Here the probe is unavailable on the first pass
        // and available on the retry, so the retry must fire AND the wallet must get reconciled.
        var wallet = SingleKeyWallet();
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo> { wallet }));

        var probeCalls = 0;
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                probeCalls++;
                if (probeCalls == 1)
                    throw new InvalidOperationException("arkd unreachable");
                return Task.FromResult<ArkServerInfo>(null!);
            });

        await using var sut = CreateService(retryDelay: TimeSpan.FromMilliseconds(50));
        await sut.StartAsync(CancellationToken.None);

        // The first pass fails the availability probe (nothing reconciled); the bounded retry
        // re-runs the pass once the backend is reachable, and the wallet is then reconciled.
        await WaitForAsync(
            () => _ensurer.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(ISingleKeyDefaultEnsurer.EnsureDefaultAsync)),
            timeoutMs: 3000);

        Assert.That(probeCalls, Is.GreaterThanOrEqualTo(2),
            "backend-unavailable startup pass should be retried");
        await _ensurer.Received().EnsureDefaultAsync("w1", Arg.Any<CancellationToken>());
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

    // ── Destination-safety tests ──────────────────────────────────────────────

    [Test]
    public async Task ReconcileWallet_flags_and_raises_when_destination_is_stale()
    {
        var deprecated = NewKey();
        var serverInfo = MakeServerInfo(currentSigner: NewKey(), deprecated: [deprecated]);
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(serverInfo));

        var address = MakeAddress(serverKey: deprecated);
        var wallet = new ArkWalletInfo("w1", null, address.ToString(isMainnet: false), WalletType.SingleKey,
            "tr(0000000000000000000000000000000000000000000000000000000000000001)", 0, Metadata: null);
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        DestinationDisabledEventArgs? raised = null;
        var sut = CreateService();
        ((IDestinationSafetyNotifier)sut).DestinationDisabled += (_, e) => raised = e;

        await sut.ReconcileWalletAsync("w1", CancellationToken.None);

        await _walletStorage.Received(1).SetMetadataValue(
            "w1",
            DestinationSafety.PendingConfirmationMetadataKey,
            Arg.Is<string?>(v => v != null),
            Arg.Any<CancellationToken>());

        Assert.That(raised, Is.Not.Null, "DestinationDisabled event should have fired");
        Assert.That(raised!.WalletId, Is.EqualTo("w1"));
    }

    [Test]
    public async Task ReconcileWallet_clears_flag_when_destination_not_stale()
    {
        var current = NewKey();
        var serverInfo = MakeServerInfo(currentSigner: current, deprecated: [NewKey()]);
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(serverInfo));

        var address = MakeAddress(serverKey: current);
        var metadata = new Dictionary<string, string>
        {
            [DestinationSafety.PendingConfirmationMetadataKey] = "some-old-hex"
        };
        var wallet = new ArkWalletInfo("w1", null, address.ToString(isMainnet: false), WalletType.SingleKey,
            "tr(0000000000000000000000000000000000000000000000000000000000000001)", 0, Metadata: metadata);
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        var sut = CreateService();
        await sut.ReconcileWalletAsync("w1", CancellationToken.None);

        await _walletStorage.Received(1).SetMetadataValue(
            "w1",
            DestinationSafety.PendingConfirmationMetadataKey,
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReconcileWallet_no_duplicate_event_when_already_flagged()
    {
        var deprecated = NewKey();
        var deprecatedHex = Convert.ToHexString(deprecated.ToBytes()).ToLowerInvariant();
        var serverInfo = MakeServerInfo(currentSigner: NewKey(), deprecated: [deprecated]);
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(serverInfo));

        var address = MakeAddress(serverKey: deprecated);
        var metadata = new Dictionary<string, string>
        {
            [DestinationSafety.PendingConfirmationMetadataKey] = deprecatedHex
        };
        var wallet = new ArkWalletInfo("w1", null, address.ToString(isMainnet: false), WalletType.SingleKey,
            "tr(0000000000000000000000000000000000000000000000000000000000000001)", 0, Metadata: metadata);
        _walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        var eventFired = false;
        var sut = CreateService();
        ((IDestinationSafetyNotifier)sut).DestinationDisabled += (_, _) => eventFired = true;

        await sut.ReconcileWalletAsync("w1", CancellationToken.None);

        Assert.That(eventFired, Is.False, "DestinationDisabled event must NOT fire when already flagged");
        await _walletStorage.DidNotReceive().SetMetadataValue(
            "w1",
            DestinationSafety.PendingConfirmationMetadataKey,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── Test helpers ─────────────────────────────────────────────────────────

    private static ECXOnlyPubKey NewKey()
        => ECXOnlyPubKey.Create(new NBitcoin.Key().PubKey.TaprootInternalKey.ToBytes());

    private static ArkServerInfo MakeServerInfo(ECXOnlyPubKey currentSigner, ECXOnlyPubKey[] deprecated)
    {
        var deprecatedDict = new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance);
        foreach (var key in deprecated)
            deprecatedDict[key] = 0;

        var emptyMultisig = new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());
        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: currentSigner.ToOutputDescriptor(Network.RegTest),
            DeprecatedSigners: deprecatedDict,
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: currentSigner,
            CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "test-digest");
    }

    private static ArkAddress MakeAddress(ECXOnlyPubKey serverKey)
    {
        var tweakedKey = NewKey();
        return new ArkAddress(tweakedKey, serverKey, version: 0, isMainnet: false);
    }
}

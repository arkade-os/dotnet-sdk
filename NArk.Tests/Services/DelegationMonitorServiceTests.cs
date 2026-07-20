using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Tests.Common;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests.Services;

/// <summary>
/// Exercises the automatic-delegation trigger path: a new unspent VTXO landing on a
/// delegate contract should cause <see cref="DelegationMonitorService"/> to build and
/// send a real (signed) intent proof + forfeit transaction to the delegator, exactly
/// once per outpoint, without needing a live delegator or arkd.
/// </summary>
[TestFixture]
public class DelegationMonitorServiceTests
{
    private const string WalletId = "wallet-1";

    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly Sequence ExitDelay = new(144);

    private IVtxoStorage _vtxoStorage = null!;
    private IContractStorage _contractStorage = null!;
    private IDelegatorProvider _delegatorProvider = null!;
    private IWalletProvider _walletProvider = null!;
    private IClientTransport _clientTransport = null!;
    private SimpleSeedWallet _seedWallet = null!;
    private OutputDescriptor _userKey;
    private Key _delegatePrivateKey = null!;
    private OutputDescriptor _delegateKey;
    private TaskCompletionSource _delegateCalled = null!;

    private static ArkServerInfo CreateServerInfo() => new(
        Dust: Money.Satoshis(1000),
        SignerKey: ServerKey,
        DeprecatedSigners: new Dictionary<NBitcoin.Secp256k1.ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
        Network: Network.RegTest,
        UnilateralExit: ExitDelay,
        BoardingExit: new Sequence(1008),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ForfeitPubKey: ServerKey.ToXOnlyPubKey(),
        CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), new NofNMultisigTapScript([ServerKey.ToXOnlyPubKey()])),
        FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
        Digest: ""
    );

    [SetUp]
    public async Task SetUp()
    {
        _delegateCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _delegatorProvider = Substitute.For<IDelegatorProvider>();
        _walletProvider = Substitute.For<IWalletProvider>();
        _clientTransport = Substitute.For<IClientTransport>();

        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateServerInfo()));

        // Real signer/address-provider so the monitor produces a genuinely signed
        // BIP322 intent proof + forfeit tx, not a mocked stand-in.
        _seedWallet = SimpleSeedWallet.CreateNewWallet(
            new Mnemonic(Wordlist.English, WordCount.Twelve), Network.RegTest, _clientTransport);
        _userKey = await _seedWallet.GetNextSigningDescriptor();

        _delegatePrivateKey = new Key();
        _delegateKey = KeyExtensions.ParseOutputDescriptor(_delegatePrivateKey.PubKey.ToHex(), Network.RegTest);

        _walletProvider.GetSignerAsync(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeWalletSigner?>(_seedWallet));
        _walletProvider.GetAddressProviderAsync(WalletId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeAddressProvider?>(_seedWallet));

        _delegatorProvider.GetDelegatorInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DelegatorInfo(_delegatePrivateKey.PubKey.ToHex(), "0", "")));

        _delegatorProvider
            .DelegateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _delegateCalled.TrySetResult();
                return Task.CompletedTask;
            });
    }

    [TearDown]
    public void TearDown()
    {
        _delegatePrivateKey.Dispose();
    }

    private ArkDelegateContract CreateDelegateContract(OutputDescriptor? delegateKey = null) =>
        new(ServerKey, ExitDelay, _userKey, delegateKey ?? _delegateKey);

    private static ArkVtxo CreateVtxo(ArkContract contract, bool spent = false) => new(
        Script: contract.GetScriptPubKey().ToHex(),
        TransactionId: uint256.One.ToString(),
        TransactionOutputIndex: 0,
        Amount: 10_000,
        SpentByTransactionId: spent ? uint256.Zero.ToString() : null,
        SettledByTransactionId: null,
        Swept: false,
        CreatedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
        ExpiresAtHeight: null);

    private void SeedContract(ArkContract contract)
    {
        var entity = contract.ToEntity(WalletId);
        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Is<string[]?>(s => s != null && s.Contains(entity.Script)),
                isActive: Arg.Any<bool?>(),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                scope: Arg.Any<ContractScope?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([entity]));
    }

    private DelegationMonitorService CreateService() => new(
        _vtxoStorage,
        _contractStorage,
        [new DelegateContractDelegationTransformer(_walletProvider)],
        _delegatorProvider,
        _walletProvider,
        _clientTransport);

    [Test]
    public async Task DelegatesNewUnspentVtxo_OnDelegateContractMatchingDelegator()
    {
        var contract = CreateDelegateContract();
        SeedContract(contract);
        var vtxo = CreateVtxo(contract);

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);

        await _delegateCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _delegatorProvider.Received(1).DelegateAsync(
            Arg.Is<string>(m => m.Contains("\"register\"")),
            Arg.Is<string>(proof => !string.IsNullOrEmpty(proof)),
            Arg.Is<string[]>(f => f.Length == 1 && !string.IsNullOrEmpty(f[0])),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DoesNotDelegate_WhenVtxoAlreadySpent()
    {
        var contract = CreateDelegateContract();
        SeedContract(contract);
        var vtxo = CreateVtxo(contract, spent: true);

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await Task.Delay(300);

        await _delegatorProvider.DidNotReceive().DelegateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DoesNotDelegate_WhenDelegatorPubkeyDoesNotMatchContract()
    {
        // Contract was built for a different delegate than the one the delegator service reports.
        var otherDelegateKey = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);
        var contract = CreateDelegateContract(otherDelegateKey);
        SeedContract(contract);
        var vtxo = CreateVtxo(contract);

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await Task.Delay(300);

        await _delegatorProvider.DidNotReceive().DelegateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DoesNotDelegate_WhenNoContractFoundForScript()
    {
        var contract = CreateDelegateContract();
        // Intentionally not calling SeedContract: storage returns no contract for the script.
        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Any<bool?>(),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                scope: Arg.Any<ContractScope?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([]));
        var vtxo = CreateVtxo(contract);

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await Task.Delay(300);

        await _delegatorProvider.DidNotReceive().DelegateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DoesNotDelegateTwice_ForSameOutpoint()
    {
        var contract = CreateDelegateContract();
        SeedContract(contract);
        var vtxo = CreateVtxo(contract);

        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        // First sighting: triggers delegation.
        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await _delegateCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second sighting of the same outpoint (e.g. a duplicate storage notification):
        // must not be re-delegated.
        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await Task.Delay(300);

        await _delegatorProvider.Received(1).DelegateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StopAsync_UnsubscribesFromVtxosChanged()
    {
        var contract = CreateDelegateContract();
        SeedContract(contract);
        var vtxo = CreateVtxo(contract);

        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        _vtxoStorage.VtxosChanged += Raise.Event<EventHandler<ArkVtxo>>(_vtxoStorage, vtxo);
        await Task.Delay(300);

        await _delegatorProvider.DidNotReceive().DelegateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        service.Dispose();
    }
}

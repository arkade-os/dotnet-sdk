using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class PostBatchVtxoPollingHandlerTests
{
    private IContractStorage _contractStorage;
    private IVtxoStorage _vtxoStorage;
    private IClientTransport _clientTransport;
    private IOptions<VtxoPollingOptions> _options;
    private PostBatchVtxoPollingHandler _handler;

    [SetUp]
    public void SetUp()
    {
        _contractStorage = Substitute.For<IContractStorage>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _clientTransport = Substitute.For<IClientTransport>();

        _options = Options.Create(new VtxoPollingOptions
        {
            BatchSuccessPollingDelay = TimeSpan.Zero // No delay in tests
        });

        var vtxoSyncService = new VtxoSynchronizationService(
            _vtxoStorage,
            _clientTransport,
            new IActiveScriptsProvider[] { _contractStorage });

        _handler = new PostBatchVtxoPollingHandler(
            vtxoSyncService,
            _contractStorage,
            _options);
    }

    [Test]
    public async Task SkipsPolling_WhenStateIsNotSuccessful()
    {
        var intent = CreateDummyIntent();
        var @event = new PostBatchSessionEvent(
            Intent: intent,
            CommitmentTransactionId: null,
            State: ActionState.Failed,
            FailReason: "batch failed");

        await _handler.HandleAsync(@event);

        // Contract storage should NOT be queried when state is not Successful
        await _contractStorage.DidNotReceive().GetContracts(
            walletIds: Arg.Any<string[]?>(),
            scripts: Arg.Any<string[]?>(),
            isActive: Arg.Any<bool?>(),
            contractTypes: Arg.Any<string[]?>(),
            searchText: Arg.Any<string?>(),
            skip: Arg.Any<int?>(),
            take: Arg.Any<int?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkipsPolling_WhenNoActiveContracts()
    {
        var intent = CreateDummyIntent();
        var @event = new PostBatchSessionEvent(
            Intent: intent,
            CommitmentTransactionId: "tx-id",
            State: ActionState.Successful,
            FailReason: null);

        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Is<bool?>(b => b == true),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([]));

        await _handler.HandleAsync(@event);

        // No VTXO polling should happen when there are no active contracts
        _clientTransport.DidNotReceive().GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PollsVtxos_AfterBatchSuccess()
    {
        var intent = CreateDummyIntent();
        var @event = new PostBatchSessionEvent(
            Intent: intent,
            CommitmentTransactionId: "tx-id",
            State: ActionState.Successful,
            FailReason: null);

        var contracts = new List<ArkContractEntity>
        {
            new(Script: "script1", ActivityState: ContractActivityState.Active,
                Type: "generic", AdditionalData: new Dictionary<string, string>(),
                WalletIdentifier: "test-wallet", CreatedAt: DateTimeOffset.UtcNow)
        };

        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Is<bool?>(b => b == true),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>(contracts));

        // Mock the VTXO snapshot to return empty (just verify it's called)
        _clientTransport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ArkVtxo>());

        await _handler.HandleAsync(@event);

        // Verify that VTXO polling was triggered with the scripts from active contracts
        _clientTransport.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(s => s.Contains("script1")),
            Arg.Any<CancellationToken>());
    }

    private static ArkIntent CreateDummyIntent()
    {
        return new ArkIntent(
            IntentTxId: "batch-intent-tx-id",
            IntentId: "intent-id",
            WalletId: "test-wallet",
            State: ArkIntentState.BatchInProgress,
            ValidFrom: DateTimeOffset.UtcNow.AddHours(-1),
            ValidUntil: DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RegisterProof: "dummy",
            RegisterProofMessage: "dummy",
            DeleteProof: "dummy",
            DeleteProofMessage: "dummy",
            BatchId: "batch-1",
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [],
            SignerDescriptor: "dummy-signer");
    }
}

[TestFixture]
public class PostSpendVtxoPollingHandlerTests
{
    private IContractStorage _contractStorage;
    private IVtxoStorage _vtxoStorage;
    private IClientTransport _clientTransport;
    private IOptions<VtxoPollingOptions> _options;
    private PostSpendVtxoPollingHandler _handler;

    [SetUp]
    public void SetUp()
    {
        _contractStorage = Substitute.For<IContractStorage>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _clientTransport = Substitute.For<IClientTransport>();

        _options = Options.Create(new VtxoPollingOptions
        {
            TransactionBroadcastPollingDelay = TimeSpan.Zero // No delay in tests
        });

        var vtxoSyncService = new VtxoSynchronizationService(
            _vtxoStorage,
            _clientTransport,
            new IActiveScriptsProvider[] { _contractStorage });

        _handler = new PostSpendVtxoPollingHandler(
            vtxoSyncService,
            _contractStorage,
            _options);
    }

    [Test]
    public async Task SkipsPolling_WhenSpendFailed()
    {
        var coin = CreateMockCoin("wallet-1");
        var @event = new PostCoinsSpendActionEvent(
            ArkCoins: [coin],
            TransactionId: uint256.One,
            State: ActionState.Failed,
            FailReason: "broadcast failed");

        await _handler.HandleAsync(@event);

        // Contract storage should NOT be queried when state is not Successful
        await _contractStorage.DidNotReceive().GetContracts(
            walletIds: Arg.Any<string[]?>(),
            scripts: Arg.Any<string[]?>(),
            isActive: Arg.Any<bool?>(),
            contractTypes: Arg.Any<string[]?>(),
            searchText: Arg.Any<string?>(),
            skip: Arg.Any<int?>(),
            take: Arg.Any<int?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkipsPolling_WhenNoWalletIds()
    {
        // Create a coin with empty wallet identifier
        var coin = CreateMockCoin("");
        var @event = new PostCoinsSpendActionEvent(
            ArkCoins: [coin],
            TransactionId: uint256.One,
            State: ActionState.Successful,
            FailReason: null);

        await _handler.HandleAsync(@event);

        // Contract storage should NOT be queried when no wallet IDs
        await _contractStorage.DidNotReceive().GetContracts(
            walletIds: Arg.Any<string[]?>(),
            scripts: Arg.Any<string[]?>(),
            isActive: Arg.Any<bool?>(),
            contractTypes: Arg.Any<string[]?>(),
            searchText: Arg.Any<string?>(),
            skip: Arg.Any<int?>(),
            take: Arg.Any<int?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PollsVtxos_AfterSpendSuccess()
    {
        var coin = CreateMockCoin("wallet-1");
        var @event = new PostCoinsSpendActionEvent(
            ArkCoins: [coin],
            TransactionId: uint256.One,
            State: ActionState.Successful,
            FailReason: null);

        var contracts = new List<ArkContractEntity>
        {
            new(Script: "spend-script-1", ActivityState: ContractActivityState.Active,
                Type: "generic", AdditionalData: new Dictionary<string, string>(),
                WalletIdentifier: "wallet-1", CreatedAt: DateTimeOffset.UtcNow)
        };

        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Is<bool?>(b => b == true),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>(contracts));

        _clientTransport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ArkVtxo>());

        await _handler.HandleAsync(@event);

        _clientTransport.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(s => s.Contains("spend-script-1")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkipsPolling_WhenNoActiveContracts()
    {
        var coin = CreateMockCoin("wallet-1");
        var @event = new PostCoinsSpendActionEvent(
            ArkCoins: [coin],
            TransactionId: uint256.One,
            State: ActionState.Successful,
            FailReason: null);

        _contractStorage.GetContracts(
                walletIds: Arg.Any<string[]?>(),
                scripts: Arg.Any<string[]?>(),
                isActive: Arg.Is<bool?>(b => b == true),
                contractTypes: Arg.Any<string[]?>(),
                searchText: Arg.Any<string?>(),
                skip: Arg.Any<int?>(),
                take: Arg.Any<int?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>([]));

        await _handler.HandleAsync(@event);

        _clientTransport.DidNotReceive().GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<CancellationToken>());
    }

    private static ArkCoin CreateMockCoin(string walletIdentifier)
    {
        var scriptBuilder = Substitute.For<NArk.Abstractions.Scripts.ScriptBuilder>();
        scriptBuilder.BuildScript().Returns(Enumerable.Empty<Op>());
        scriptBuilder.Build().Returns(new TapScript(Script.Empty, TapLeafVersion.C0));

        var contract = Substitute.For<ArkContract>(
            NArk.Abstractions.Extensions.KeyExtensions.ParseOutputDescriptor(
                "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
                Network.RegTest));

        return new ArkCoin(
            walletIdentifier: walletIdentifier,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, 0),
            txOut: new TxOut(Money.Satoshis(10000), Script.Empty),
            signerDescriptor: null,
            spendingScriptBuilder: scriptBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false);
    }
}

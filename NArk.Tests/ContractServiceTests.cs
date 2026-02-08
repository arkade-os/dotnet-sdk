using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Events;
using NArk.Core.Extensions;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class ContractServiceTests
{
    private IWalletProvider _walletProvider;
    private IContractStorage _contractStorage;
    private IClientTransport _clientTransport;
    private IEventHandler<NewContractActionEvent> _eventHandler;
    private IArkadeAddressProvider _addressProvider;
    private ContractService _service;

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor DifferentServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    [SetUp]
    public void SetUp()
    {
        _walletProvider = Substitute.For<IWalletProvider>();
        _contractStorage = Substitute.For<IContractStorage>();
        _clientTransport = Substitute.For<IClientTransport>();
        _eventHandler = Substitute.For<IEventHandler<NewContractActionEvent>>();
        _addressProvider = Substitute.For<IArkadeAddressProvider>();

        _walletProvider.GetAddressProviderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeAddressProvider?>(_addressProvider));

        _service = new ContractService(
            _walletProvider,
            _contractStorage,
            _clientTransport,
            new[] { _eventHandler });
    }

    [Test]
    public async Task DeriveContract_CallsGetNextContract_AndSavesToStorage()
    {
        var (mockContract, mockEntity) = CreateMockContractAndEntity();

        _addressProvider.GetNextContract(
                NextContractPurpose.Receive,
                ContractActivityState.Active,
                Arg.Any<ArkContract[]?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((mockContract, mockEntity)));

        var result = await _service.DeriveContract("wallet-1", NextContractPurpose.Receive);

        Assert.That(result, Is.SameAs(mockContract));
        await _contractStorage.Received(1).SaveContract(
            Arg.Is<ArkContractEntity>(e =>
                e.Script == "deadbeef" &&
                e.WalletIdentifier == "wallet-1"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeriveContract_WithMetadata_AttachesMetadata()
    {
        var (mockContract, mockEntity) = CreateMockContractAndEntity();

        _addressProvider.GetNextContract(
                Arg.Any<NextContractPurpose>(),
                Arg.Any<ContractActivityState>(),
                Arg.Any<ArkContract[]?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((mockContract, mockEntity)));

        // Capture the saved entity via When..Do before the call
        ArkContractEntity? savedEntity = null;
        _contractStorage.SaveContract(Arg.Any<ArkContractEntity>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call => savedEntity = call.Arg<ArkContractEntity>());

        var metadata = new Dictionary<string, string> { ["source"] = "test", ["tag"] = "unit-test" };

        await _service.DeriveContract("wallet-1", NextContractPurpose.Receive, metadata: metadata);

        Assert.That(savedEntity, Is.Not.Null);
        Assert.That(savedEntity!.Metadata, Is.Not.Null);
        Assert.That(savedEntity.Metadata!["source"], Is.EqualTo("test"));
        Assert.That(savedEntity.Metadata["tag"], Is.EqualTo("unit-test"));
    }

    [Test]
    public async Task DeriveContract_WithoutMetadata_EntityHasNoMetadata()
    {
        var (mockContract, mockEntity) = CreateMockContractAndEntity();

        _addressProvider.GetNextContract(
                Arg.Any<NextContractPurpose>(),
                Arg.Any<ContractActivityState>(),
                Arg.Any<ArkContract[]?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((mockContract, mockEntity)));

        await _service.DeriveContract("wallet-1", NextContractPurpose.Receive);

        // Entity should be saved without metadata since none was provided and the mock entity has no metadata
        await _contractStorage.Received(1).SaveContract(
            Arg.Is<ArkContractEntity>(e => e.Metadata == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void ImportContract_ThrowsOnServerKeyMismatch()
    {
        // Create a contract whose Server key differs from the server's SignerKey
        var mockContract = Substitute.For<ArkContract>(DifferentServerKey);

        var serverInfo = CreateServerInfo(TestServerKey);
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(serverInfo));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.ImportContract("wallet-1", mockContract));

        // Verify SaveContract was NOT called
        _contractStorage.DidNotReceive().SaveContract(
            Arg.Any<ArkContractEntity>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeriveContract_RaisesNewContractActionEvent()
    {
        var (mockContract, mockEntity) = CreateMockContractAndEntity();

        _addressProvider.GetNextContract(
                Arg.Any<NextContractPurpose>(),
                Arg.Any<ContractActivityState>(),
                Arg.Any<ArkContract[]?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((mockContract, mockEntity)));

        await _service.DeriveContract("wallet-1", NextContractPurpose.Receive);

        await _eventHandler.Received(1).HandleAsync(
            Arg.Is<NewContractActionEvent>(e =>
                e.WalletId == "wallet-1" &&
                e.Contract == mockContract),
            Arg.Any<CancellationToken>());
    }

    private static (ArkContract contract, ArkContractEntity entity) CreateMockContractAndEntity()
    {
        var mockContract = Substitute.For<ArkContract>(TestServerKey);
        var mockEntity = new ArkContractEntity(
            Script: "deadbeef",
            ActivityState: ContractActivityState.Active,
            Type: "generic",
            AdditionalData: new Dictionary<string, string>(),
            WalletIdentifier: "wallet-1",
            CreatedAt: DateTimeOffset.UtcNow);

        return (mockContract, mockEntity);
    }

    private static ArkServerInfo CreateServerInfo(OutputDescriptor signerKey)
    {
        var emptyMultisig = new NArk.Core.Scripts.NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());

        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: signerKey,
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new NArk.Core.Scripts.UnilateralPathArkTapScript(
                new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"));
    }
}

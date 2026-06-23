using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class OnchainSweepServiceTests
{
    private IVtxoStorage _vtxoStorage = null!;
    private IContractStorage _contractStorage = null!;
    private IBitcoinBlockchain _blockchain = null!;
    private IContractService _contractService = null!;
    private IWalletProvider _walletProvider = null!;
    private IClientTransport _transport = null!;
    private IArkadeWalletSigner _signer = null!;
    private IOnchainSweepHandler _sweepHandler = null!;

    private static readonly TimeHeight CurrentTime = new(
        DateTimeOffset.UtcNow, 800_000);

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor TestUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    private static readonly Sequence BoardingExitDelay = new(144);

    private const string TestWalletId = "test-wallet";

    [SetUp]
    public void SetUp()
    {
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _contractStorage = Substitute.For<IContractStorage>();
        _blockchain = Substitute.For<IBitcoinBlockchain>();
        _contractService = Substitute.For<IContractService>();
        _walletProvider = Substitute.For<IWalletProvider>();
        _transport = Substitute.For<IClientTransport>();
        _signer = Substitute.For<IArkadeWalletSigner>();
        _sweepHandler = Substitute.For<IOnchainSweepHandler>();

        _blockchain.GetChainTime(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CurrentTime));
        _blockchain.EstimateFeeRateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FeeRate(Money.Satoshis(10), 1)));
        _blockchain.BroadcastAsync(Arg.Any<Transaction>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateServerInfo()));

        _walletProvider.GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeWalletSigner?>(_signer));

        // Sign with a real key so the witness bytes are non-null
        var signingKey = new Key();
        _signer.Sign(Arg.Any<OutputDescriptor>(), Arg.Any<uint256>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var hash = callInfo.Arg<uint256>();
                var taprootSig = signingKey.SignTaprootKeySpend(hash, TaprootSigHash.Default);
                if (!SecpSchnorrSignature.TryCreate(taprootSig.SchnorrSignature.ToBytes(), out var schnorr))
                    throw new InvalidOperationException("Failed to create test schnorr signature");
                return Task.FromResult((ECXOnlyPubKey.Create(signingKey.PubKey.TaprootInternalKey.ToBytes()), schnorr!));
            });

        // Default: no VTXOs
        SetupVtxoStorage();

        // Default: no contracts
        SetupContractStorage();
    }

    private OnchainSweepService CreateService(IOnchainSweepHandler? handler = null)
    {
        return new OnchainSweepService(
            _vtxoStorage,
            _contractStorage,
            _blockchain,
            _contractService,
            _walletProvider,
            _transport,
            handler);
    }

    private static ArkServerInfo CreateServerInfo()
    {
        var emptyMultisig = new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());
        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: TestServerKey,
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
            Network: Network.RegTest,
            UnilateralExit: BoardingExitDelay,
            BoardingExit: BoardingExitDelay,
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new UnilateralPathArkTapScript(BoardingExitDelay, emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "");
    }

    private void SetupVtxoStorage(params ArkVtxo[] vtxos)
    {
        _vtxoStorage.GetVtxos(
                Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<OutPoint>?>(),
                Arg.Any<string[]?>(),
                Arg.Any<bool>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkVtxo>>(vtxos));
    }

    private void SetupContractStorage(params ArkContractEntity[] entities)
    {
        _contractStorage.GetContracts(
                Arg.Any<string[]?>(),
                Arg.Any<string[]?>(),
                Arg.Any<bool?>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkContractEntity>>(entities));
    }

    private static ArkBoardingContract CreateBoardingContract()
    {
        return new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
    }

    private static ArkContractEntity CreateContractEntity(ArkBoardingContract contract)
    {
        return contract.ToEntity(TestWalletId);
    }

    private static ArkVtxo CreateVtxo(
        string script,
        DateTimeOffset? expiresAt = null,
        uint? expiresAtHeight = null,
        bool unrolled = true,
        string? spentByTxId = null)
    {
        return new ArkVtxo(
            Script: script,
            TransactionId: "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234",
            TransactionOutputIndex: 0,
            Amount: 100_000,
            SpentByTransactionId: spentByTxId,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt: expiresAt,
            ExpiresAtHeight: expiresAtHeight,
            Unrolled: unrolled);
    }

    [Test]
    public async Task SweepExpiredUtxosAsync_SweepsExpiredUnrolledVtxo()
    {
        // Arrange
        var contract = CreateBoardingContract();
        var contractEntity = CreateContractEntity(contract);

        var expiredVtxo = CreateVtxo(
            contractEntity.Script,
            expiresAt: CurrentTime.Timestamp.AddHours(-1),
            unrolled: true);

        SetupVtxoStorage(expiredVtxo);
        SetupContractStorage(contractEntity);

        var freshContract = CreateBoardingContract();
        _contractService.DeriveContract(
                Arg.Any<string>(),
                NextContractPurpose.Boarding,
                Arg.Any<ContractActivityState>(),
                Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkContract>(freshContract));

        var service = CreateService();

        // Act
        await service.SweepExpiredUtxosAsync(CancellationToken.None);

        // Assert — derived a fresh boarding contract for the sweep destination
        await _contractService.Received(1).DeriveContract(
            TestWalletId,
            NextContractPurpose.Boarding,
            Arg.Any<ContractActivityState>(),
            Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());

        // Assert — broadcast the sweep transaction
        await _blockchain.Received(1).BroadcastAsync(
            Arg.Any<Transaction>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SweepExpiredUtxosAsync_SkipsNonExpiredVtxos()
    {
        // Arrange
        var contract = CreateBoardingContract();
        var entity = CreateContractEntity(contract);

        // Not expired: ExpiresAt is in the future
        var nonExpiredVtxo = CreateVtxo(
            entity.Script,
            expiresAt: CurrentTime.Timestamp.AddHours(24),
            unrolled: true);

        SetupVtxoStorage(nonExpiredVtxo);

        var service = CreateService();

        // Act — should complete without any sweep attempt
        await service.SweepExpiredUtxosAsync(CancellationToken.None);

        // Assert — no contract lookup needed since no expired VTXOs
        await _contractStorage.DidNotReceive().GetContracts(
            Arg.Any<string[]?>(),
            Arg.Any<string[]?>(),
            Arg.Any<bool?>(),
            Arg.Any<string[]?>(),
            Arg.Any<string?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SweepExpiredUtxosAsync_CustomHandlerIsCalledWhenRegistered()
    {
        // Arrange
        var contract = CreateBoardingContract();
        var contractEntity = CreateContractEntity(contract);

        var expiredVtxo = CreateVtxo(
            contractEntity.Script,
            expiresAt: CurrentTime.Timestamp.AddHours(-1),
            unrolled: true);

        SetupVtxoStorage(expiredVtxo);
        SetupContractStorage(contractEntity);

        _sweepHandler.HandleExpiredUtxoAsync(
                Arg.Any<string>(),
                Arg.Any<ArkVtxo>(),
                Arg.Any<ArkContractEntity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var service = CreateService(_sweepHandler);

        // Act — should NOT throw because handler returns true
        await service.SweepExpiredUtxosAsync(CancellationToken.None);

        // Assert — handler was called
        await _sweepHandler.Received(1).HandleExpiredUtxoAsync(
            TestWalletId,
            expiredVtxo,
            contractEntity,
            Arg.Any<CancellationToken>());

        // Default sweep logic was NOT reached (no DeriveContract call)
        await _contractService.DidNotReceive().DeriveContract(
            Arg.Any<string>(),
            Arg.Any<NextContractPurpose>(),
            Arg.Any<ContractActivityState>(),
            Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SweepExpiredUtxosAsync_SkipsAlreadySpentVtxos()
    {
        // Arrange
        var contract = CreateBoardingContract();
        var entity = CreateContractEntity(contract);

        // Expired but already spent
        var spentVtxo = CreateVtxo(
            entity.Script,
            expiresAt: CurrentTime.Timestamp.AddHours(-1),
            unrolled: true,
            spentByTxId: "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

        SetupVtxoStorage(spentVtxo);

        var service = CreateService();

        // Act — should complete without sweep attempt (spent VTXO filtered out)
        await service.SweepExpiredUtxosAsync(CancellationToken.None);

        // Assert — no contract lookup since no eligible VTXOs
        await _contractStorage.DidNotReceive().GetContracts(
            Arg.Any<string[]?>(),
            Arg.Any<string[]?>(),
            Arg.Any<bool?>(),
            Arg.Any<string[]?>(),
            Arg.Any<string?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SweepExpiredUtxosAsync_SkipsNonUnrolledVtxos()
    {
        // Arrange
        var contract = CreateBoardingContract();
        var entity = CreateContractEntity(contract);

        // Expired but not unrolled (regular VTXO)
        var regularVtxo = CreateVtxo(
            entity.Script,
            expiresAt: CurrentTime.Timestamp.AddHours(-1),
            unrolled: false);

        SetupVtxoStorage(regularVtxo);

        var service = CreateService();

        // Act
        await service.SweepExpiredUtxosAsync(CancellationToken.None);

        // Assert — no contract lookup since the VTXO is not unrolled
        await _contractStorage.DidNotReceive().GetContracts(
            Arg.Any<string[]?>(),
            Arg.Any<string[]?>(),
            Arg.Any<bool?>(),
            Arg.Any<string[]?>(),
            Arg.Any<string?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }
}

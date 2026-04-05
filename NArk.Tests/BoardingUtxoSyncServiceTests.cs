using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class BoardingUtxoSyncServiceTests
{
    private IContractStorage _contractStorage = null!;
    private IVtxoStorage _vtxoStorage = null!;
    private IClientTransport _clientTransport = null!;
    private IBoardingUtxoProvider _utxoProvider = null!;

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor TestUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    private static readonly Sequence BoardingExitDelay = new(144);

    [SetUp]
    public void SetUp()
    {
        _contractStorage = Substitute.For<IContractStorage>();
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _clientTransport = Substitute.For<IClientTransport>();
        _utxoProvider = Substitute.For<IBoardingUtxoProvider>();

        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateServerInfo()));
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

    [Test]
    public async Task SyncAsync_ConfirmedUtxo_IsUpsertedWithCorrectFields()
    {
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        SetupContractStorage(entity);
        SetupVtxoStorage();

        // Capture the upserted VTXO before the call
        ArkVtxo? captured = null;
        _vtxoStorage.UpsertVtxo(Arg.Do<ArkVtxo>(v => captured = v), Arg.Any<CancellationToken>())
            .Returns(true);

        _utxoProvider.GetUtxosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new BoardingUtxo(
                    Txid: "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234",
                    Vout: 0,
                    Amount: 100000,
                    Confirmed: true,
                    BlockHeight: 800000,
                    BlockTime: 1700000000)
            ]);

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, _utxoProvider);

        await service.SyncAsync(CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(captured!.Script, Is.EqualTo(entity.Script));
            Assert.That(captured.TransactionId, Is.EqualTo("abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234"));
            Assert.That(captured.TransactionOutputIndex, Is.EqualTo(0));
            Assert.That(captured.Amount, Is.EqualTo(100000));
            Assert.That(captured.Unrolled, Is.True);
            Assert.That(captured.Swept, Is.False);
            Assert.That(captured.SpentByTransactionId, Is.Null);
            Assert.That(captured.ExpiresAt, Is.Not.Null);
            Assert.That(captured.Metadata, Is.Not.Null);
            Assert.That(captured.Metadata!["Confirmed"], Is.EqualTo("True"));
        });
    }

    [Test]
    public async Task SyncAsync_UnconfirmedUtxo_IsUpsertedWithNullExpiry()
    {
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        SetupContractStorage(entity);
        SetupVtxoStorage();

        // Capture the upserted VTXO before the call
        ArkVtxo? captured = null;
        _vtxoStorage.UpsertVtxo(Arg.Do<ArkVtxo>(v => captured = v), Arg.Any<CancellationToken>())
            .Returns(true);

        _utxoProvider.GetUtxosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new BoardingUtxo(
                    Txid: "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234",
                    Vout: 0,
                    Amount: 50000,
                    Confirmed: false,
                    BlockHeight: 0,
                    BlockTime: 0)
            ]);

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, _utxoProvider);

        await service.SyncAsync(CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(captured!.Script, Is.EqualTo(entity.Script));
            Assert.That(captured.Amount, Is.EqualTo(50000));
            Assert.That(captured.Unrolled, Is.True);
            Assert.That(captured.ExpiresAt, Is.Null);
            Assert.That(captured.ExpiresAtHeight, Is.Null);
            Assert.That(captured.Metadata, Is.Not.Null);
            Assert.That(captured.Metadata!["Confirmed"], Is.EqualTo("False"));
        });
    }

    [Test]
    public async Task SyncAsync_AlreadyStoredUtxo_IsNotDuplicated()
    {
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");
        const string txid = "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234";

        var existingVtxo = new ArkVtxo(
            Script: entity.Script,
            TransactionId: txid,
            TransactionOutputIndex: 0,
            Amount: 100000,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(1700000000),
            ExpiresAt: DateTimeOffset.FromUnixTimeSeconds(1700000000).AddSeconds(144 * 600),
            ExpiresAtHeight: 800144,
            Unrolled: true);

        SetupContractStorage(entity);
        SetupVtxoStorage(existingVtxo);

        _utxoProvider.GetUtxosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new BoardingUtxo(
                    Txid: txid,
                    Vout: 0,
                    Amount: 100000,
                    Confirmed: true,
                    BlockHeight: 800000,
                    BlockTime: 1700000000)
            ]);

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, _utxoProvider);

        await service.SyncAsync(CancellationToken.None);

        // UpsertVtxo is called exactly once (the confirmed UTXO),
        // but no "spent" upsert because the existing VTXO is still onchain
        await _vtxoStorage.Received(1).UpsertVtxo(
            Arg.Is<ArkVtxo>(v =>
                v.TransactionId == txid &&
                v.SpentByTransactionId == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_SpentUtxo_IsMarkedAsSpent()
    {
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        var existingVtxo = new ArkVtxo(
            Script: entity.Script,
            TransactionId: "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            TransactionOutputIndex: 1,
            Amount: 200000,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(1699000000),
            ExpiresAt: DateTimeOffset.FromUnixTimeSeconds(1699000000).AddSeconds(144 * 600),
            ExpiresAtHeight: 799144,
            Unrolled: true);

        SetupContractStorage(entity);
        SetupVtxoStorage(existingVtxo);

        // Provider returns empty — the UTXO has been spent
        _utxoProvider.GetUtxosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<BoardingUtxo>());

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, _utxoProvider);

        await service.SyncAsync(CancellationToken.None);

        await _vtxoStorage.Received(1).UpsertVtxo(
            Arg.Is<ArkVtxo>(v =>
                v.TransactionId == "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef" &&
                v.TransactionOutputIndex == 1 &&
                v.SpentByTransactionId == "onchain-spent"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_BoardingDisabled_SkipsSync()
    {
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        SetupContractStorage(entity);
        SetupVtxoStorage();

        // Server says boarding is disabled (UtxoMaxAmount=0)
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateServerInfo(utxoMaxAmount: Money.Zero)));

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, _utxoProvider);

        await service.SyncAsync(CancellationToken.None);

        // Provider should never be called
        await _utxoProvider.DidNotReceive().GetUtxosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_UtxoBelowMinimum_IsSkipped()
    {
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        SetupContractStorage(entity);
        SetupVtxoStorage();

        // Server requires minimum 50K sats
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateServerInfo(
                utxoMinAmount: Money.Satoshis(50_000),
                utxoMaxAmount: Money.Coins(21_000_000m))));

        _utxoProvider.GetUtxosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new BoardingUtxo(
                    Txid: "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234",
                    Vout: 0,
                    Amount: 10_000, // Below minimum
                    Confirmed: true,
                    BlockHeight: 800000,
                    BlockTime: 1700000000)
            ]);

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, _utxoProvider);

        await service.SyncAsync(CancellationToken.None);

        // Should not upsert the below-minimum UTXO
        await _vtxoStorage.DidNotReceive().UpsertVtxo(Arg.Any<ArkVtxo>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_UtxoAboveMaximum_IsSkipped()
    {
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        SetupContractStorage(entity);
        SetupVtxoStorage();

        // Server caps at 100K sats
        _clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateServerInfo(
                utxoMinAmount: Money.Zero,
                utxoMaxAmount: Money.Satoshis(100_000))));

        _utxoProvider.GetUtxosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new BoardingUtxo(
                    Txid: "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234",
                    Vout: 0,
                    Amount: 200_000, // Above maximum
                    Confirmed: true,
                    BlockHeight: 800000,
                    BlockTime: 1700000000)
            ]);

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, _utxoProvider);

        await service.SyncAsync(CancellationToken.None);

        // Should not upsert the above-maximum UTXO
        await _vtxoStorage.DidNotReceive().UpsertVtxo(Arg.Any<ArkVtxo>(), Arg.Any<CancellationToken>());
    }

    private static ArkServerInfo CreateServerInfo(
        Money? utxoMinAmount = null,
        Money? utxoMaxAmount = null)
    {
        var serverKey = KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

        var emptyMultisig = new NArk.Core.Scripts.NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());

        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: serverKey,
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: BoardingExitDelay,
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new NArk.Core.Scripts.UnilateralPathArkTapScript(
                new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            UtxoMinAmount: utxoMinAmount ?? null!,
            UtxoMaxAmount: utxoMaxAmount ?? null!);
    }
}

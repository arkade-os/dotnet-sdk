using System.Net;
using System.Text;
using System.Text.Json;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
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
        // Arrange
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        SetupContractStorage(entity);
        SetupVtxoStorage();

        var utxoJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                txid = "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234",
                vout = 0,
                value = 100000L,
                status = new { confirmed = true, block_height = 800000L, block_time = 1700000000L }
            }
        });

        var handler = new FakeHttpMessageHandler(utxoJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3000/") };

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, httpClient);

        // Act
        await service.SyncAsync(CancellationToken.None);

        // Assert
        await _vtxoStorage.Received(1).UpsertVtxo(
            Arg.Is<ArkVtxo>(v =>
                v.Script == entity.Script &&
                v.TransactionId == "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234" &&
                v.TransactionOutputIndex == 0 &&
                v.Amount == 100000 &&
                v.Unrolled == true &&
                v.Swept == false &&
                v.SpentByTransactionId == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_UnconfirmedUtxo_IsSkipped()
    {
        // Arrange
        var contract = new ArkBoardingContract(TestServerKey, BoardingExitDelay, TestUserKey);
        var entity = contract.ToEntity("test-wallet");

        SetupContractStorage(entity);
        SetupVtxoStorage();

        var utxoJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                txid = "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234",
                vout = 0,
                value = 50000L,
                status = new { confirmed = false, block_height = 0L, block_time = 0L }
            }
        });

        var handler = new FakeHttpMessageHandler(utxoJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3000/") };

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, httpClient);

        // Act
        await service.SyncAsync(CancellationToken.None);

        // Assert - no upsert should have happened
        await _vtxoStorage.DidNotReceive().UpsertVtxo(
            Arg.Any<ArkVtxo>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncAsync_AlreadyStoredUtxo_IsNotDuplicated()
    {
        // Arrange
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

        var utxoJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                txid,
                vout = 0,
                value = 100000L,
                status = new { confirmed = true, block_height = 800000L, block_time = 1700000000L }
            }
        });

        var handler = new FakeHttpMessageHandler(utxoJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3000/") };

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, httpClient);

        // Act
        await service.SyncAsync(CancellationToken.None);

        // Assert - UpsertVtxo is called exactly once (the confirmed UTXO),
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
        // Arrange
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

        // Esplora returns empty - the UTXO has been spent
        var utxoJson = JsonSerializer.Serialize(Array.Empty<object>());

        var handler = new FakeHttpMessageHandler(utxoJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3000/") };

        var service = new BoardingUtxoSyncService(
            _contractStorage, _vtxoStorage, _clientTransport, httpClient);

        // Act
        await service.SyncAsync(CancellationToken.None);

        // Assert - should upsert with SpentByTransactionId set
        await _vtxoStorage.Received(1).UpsertVtxo(
            Arg.Is<ArkVtxo>(v =>
                v.TransactionId == "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef" &&
                v.TransactionOutputIndex == 1 &&
                v.SpentByTransactionId == "onchain-spent"),
            Arg.Any<CancellationToken>());
    }

    private static ArkServerInfo CreateServerInfo()
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
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"));
    }

    /// <summary>
    /// Simple fake HTTP handler that returns a canned response for any request.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public FakeHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Evm;
using NArk.Swaps.Evm.Recovery;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Swaps;

/// <summary>
/// Unit tests for <see cref="EvmChainSwapDiscoveryProvider"/> — the EVM-pair-aware
/// discovery/restore path built on <see cref="NArk.Swaps.Services.SwapsManagementService.ReconstructChainVhtlcContract"/>,
/// since Boltz's generic <c>/v2/swap/restore</c> handling in that class only recognizes the
/// ARK/BTC pair.
/// </summary>
[TestFixture]
public class EvmChainSwapDiscoveryProviderTests
{
    private static readonly Network Net = Network.RegTest;

    private static OutputDescriptor Desc() =>
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Net);

    private static ArkServerInfo ServerInfo(OutputDescriptor signer) => new(
        Dust: Money.Satoshis(330),
        SignerKey: signer,
        DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
        Network: Net,
        UnilateralExit: new Sequence(144),
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Net),
        ForfeitPubKey: signer.Extract().XOnlyPubKey,
        CheckpointTapScript: new UnilateralPathArkTapScript(
            new Sequence(144), new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>())),
        FeeTerms: new ArkOperatorFeeTerms("0", "0", "0", "0", "0"),
        Digest: "");

    private static string CltvScript(uint locktime) =>
        new Script(Op.GetPushOp(locktime), OpcodeType.OP_CHECKLOCKTIMEVERIFY).ToHex();

    private static string CsvScript(uint delay) =>
        new Script(Op.GetPushOp(delay), OpcodeType.OP_CHECKSEQUENCEVERIFY).ToHex();

    private static string PreimageHash() =>
        Convert.ToHexString(NBitcoin.Crypto.Hashes.SHA256(new byte[32])).ToLowerInvariant();

    private static EvmChainSwapDiscoveryProvider BuildProvider(
        HttpMessageHandler restoreHandler, ArkServerInfo serverInfo,
        ISwapStorage swapStorage, IContractService contractService, string pairCurrency = "TBTC")
    {
        var boltzClient = new BoltzClient(new HttpClient(restoreHandler),
            Options.Create(new BoltzClientOptions { BoltzUrl = "https://example.test/", WebsocketUrl = "wss://example.test/" }));
        var clientTransport = Substitute.For<IClientTransport>();
        clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(serverInfo);

        return new EvmChainSwapDiscoveryProvider(
            boltzClient, clientTransport, new SignerlessWalletProvider(), swapStorage, contractService,
            Options.Create(new EvmSwapOptions { RpcUrl = "http://localhost", PrivateKey = new Key().ToHex(), PairCurrency = pairCurrency }));
    }

    /// <summary>
    /// Wallet provider with no signer — the watch-only shape. Preimage re-derivation falls back
    /// to a random value, whose hash then fails the check against Boltz's reported preimage hash,
    /// so these tests exercise the "restored without a preimage" branch. That keeps them focused
    /// on discovery/reconstruction; the derivation scheme itself is covered by
    /// <see cref="PreimageDerivationTests"/>.
    /// </summary>
    private sealed class SignerlessWalletProvider : IWalletProvider
    {
        public Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
            => Task.FromResult<IArkadeWalletSigner?>(null);

        public Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default)
            => Task.FromResult<IArkadeAddressProvider?>(null);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Test]
    public async Task DiscoverAsync_ChainEvmToArk_ImportsContractAndSavesSwap()
    {
        var server = Desc();
        var us = Desc();
        var serverInfo = ServerInfo(server);
        var serverPubKeyHex = Convert.ToHexString(server.Extract().XOnlyPubKey.ToBytes()).ToLowerInvariant();

        var json = $$"""
            [{
              "id": "swap-evm-to-ark", "type": "chain", "status": "transaction.confirmed", "createdAt": 1700000000,
              "from": "TBTC", "to": "ARK", "preimageHash": "{{PreimageHash()}}",
              "claimDetails": {
                "type": "utxo", "keyIndex": 0, "lockupAddress": "tark1qtest", "amount": 50000,
                "serverPublicKey": "{{serverPubKeyHex}}",
                "tree": {
                  "refundWithoutBoltzLeaf": { "version": 192, "output": "{{CltvScript(500000)}}" },
                  "unilateralClaimLeaf": { "version": 192, "output": "{{CsvScript(10)}}" },
                  "unilateralRefundLeaf": { "version": 192, "output": "{{CsvScript(20)}}" },
                  "unilateralRefundWithoutBoltzLeaf": { "version": 192, "output": "{{CsvScript(30)}}" }
                }
              },
              "refundDetails": { "type": "evm", "contractAddress": "0xabc", "timeoutBlockHeight": 200 }
            }]
            """;

        var swapStorage = Substitute.For<ISwapStorage>();
        swapStorage.GetSwaps(
            walletIds: Arg.Any<string[]>(), swapIds: Arg.Any<string[]>(),
            cancellationToken: Arg.Any<CancellationToken>()).Returns([]);
        var contractService = Substitute.For<IContractService>();

        var provider = BuildProvider(new FakeHandler(_ => JsonResponse(json)), serverInfo, swapStorage, contractService);
        var wallet = new ArkWalletInfo("wallet-1", null, null, WalletType.SingleKey, null, 0);

        var result = await provider.DiscoverAsync(wallet, us, index: 0);

        Assert.That(result.Used, Is.True);
        Assert.That(result.Contracts, Is.Empty);

        await contractService.Received(1).ImportContract(
            "wallet-1", Arg.Any<ArkContract>(), ContractActivityState.AwaitingFundsBeforeDeactivate,
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());

        await swapStorage.Received(1).SaveSwap(
            "wallet-1",
            Arg.Is<ArkSwap>(s => s.SwapId == "swap-evm-to-ark" &&
                                  s.SwapType == ArkSwapType.ChainEvmToArk &&
                                  s.ProviderId == EvmChainSwapProvider.Id &&
                                  s.ContractScript != ""),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DiscoverAsync_ChainArkToEvm_SavesSwapAsSender()
    {
        var server = Desc();
        var us = Desc();
        var serverInfo = ServerInfo(server);
        var serverPubKeyHex = Convert.ToHexString(server.Extract().XOnlyPubKey.ToBytes()).ToLowerInvariant();

        var json = $$"""
            [{
              "id": "swap-ark-to-evm", "type": "chain", "status": "transaction.confirmed", "createdAt": 1700000000,
              "from": "ARK", "to": "TBTC", "preimageHash": "{{PreimageHash()}}",
              "refundDetails": {
                "type": "utxo", "keyIndex": 0, "lockupAddress": "tark1qtest2", "amount": 25000,
                "serverPublicKey": "{{serverPubKeyHex}}",
                "tree": {
                  "refundWithoutBoltzLeaf": { "version": 192, "output": "{{CltvScript(500000)}}" },
                  "unilateralClaimLeaf": { "version": 192, "output": "{{CsvScript(10)}}" },
                  "unilateralRefundLeaf": { "version": 192, "output": "{{CsvScript(20)}}" },
                  "unilateralRefundWithoutBoltzLeaf": { "version": 192, "output": "{{CsvScript(30)}}" }
                }
              },
              "claimDetails": { "type": "evm", "contractAddress": "0xabc", "timeoutBlockHeight": 200 }
            }]
            """;

        var swapStorage = Substitute.For<ISwapStorage>();
        swapStorage.GetSwaps(
            walletIds: Arg.Any<string[]>(), swapIds: Arg.Any<string[]>(),
            cancellationToken: Arg.Any<CancellationToken>()).Returns([]);
        var contractService = Substitute.For<IContractService>();

        var provider = BuildProvider(new FakeHandler(_ => JsonResponse(json)), serverInfo, swapStorage, contractService);
        var wallet = new ArkWalletInfo("wallet-1", null, null, WalletType.SingleKey, null, 0);

        var result = await provider.DiscoverAsync(wallet, us, index: 0);

        Assert.That(result.Used, Is.True);
        await swapStorage.Received(1).SaveSwap(
            "wallet-1",
            Arg.Is<ArkSwap>(s => s.SwapId == "swap-ark-to-evm" && s.SwapType == ArkSwapType.ChainArkToEvm),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DiscoverAsync_NoMatchingPair_ReturnsNotFound()
    {
        var server = Desc();
        var serverInfo = ServerInfo(server);

        // A BTC/ARK chain swap — not our TBTC pair.
        const string json = """
            [{
              "id": "swap-btc-to-ark", "type": "chain", "status": "transaction.confirmed", "createdAt": 1700000000,
              "from": "BTC", "to": "ARK", "preimageHash": null
            }]
            """;

        var swapStorage = Substitute.For<ISwapStorage>();
        var contractService = Substitute.For<IContractService>();
        var provider = BuildProvider(new FakeHandler(_ => JsonResponse(json)), serverInfo, swapStorage, contractService);
        var wallet = new ArkWalletInfo("wallet-1", null, null, WalletType.SingleKey, null, 0);

        var result = await provider.DiscoverAsync(wallet, Desc(), index: 0);

        Assert.That(result, Is.EqualTo(NArk.Abstractions.Recovery.DiscoveryResult.NotFound));
        await contractService.DidNotReceiveWithAnyArgs().ImportContract(
            default!, default!, default, default, default);
    }
}

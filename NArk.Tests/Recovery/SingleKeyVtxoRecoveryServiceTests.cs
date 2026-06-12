using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Recovery;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Recovery;

[TestFixture]
public class SingleKeyVtxoRecoveryServiceTests
{
    private static ECXOnlyPubKey NewKey() => ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
    private static OutputDescriptor Desc(ECXOnlyPubKey k) => k.ToOutputDescriptor(Network.RegTest);

    [Test]
    public async Task Discovers_and_persists_deprecated_signer_contract_as_active()
    {
        var activeSigner = NewKey();
        var deprecatedSigner = NewKey();
        var user = NewKey();

        var serverInfo = TestServerInfo(activeSigner, new Dictionary<ECXOnlyPubKey, long> { { deprecatedSigner, 0L } });

        var foundContract = new ArkPaymentContract(Desc(deprecatedSigner), serverInfo.UnilateralExit, Desc(user));
        var expectedScript = foundContract.GetScriptPubKey().ToHex();

        var wallet = new ArkWalletInfo("w1", null, null, WalletType.SingleKey, Desc(user).ToString(), 0);
        var walletStorage = Substitute.For<IWalletStorage>();
        walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ArkWalletInfo?>(wallet));

        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(serverInfo));

        var provider = Substitute.For<IContractDiscoveryProvider>();
        provider.Name.Returns("indexer");
        provider.DiscoverAsync(Arg.Any<ArkWalletInfo>(), Arg.Any<OutputDescriptor>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DiscoveryResult(true, new List<ArkContract> { foundContract })));

        var contractStorage = Substitute.For<IContractStorage>();

        var sut = new SingleKeyVtxoRecoveryService(
            [provider], walletStorage, contractStorage, Substitute.For<IContractService>(), transport);

        var persisted = await sut.DiscoverAsync("w1");

        Assert.That(persisted, Is.EqualTo(1));
        await contractStorage.Received(1).SaveContract(
            Arg.Is<ArkContractEntity>(e => e.Script == expectedScript && e.ActivityState == ContractActivityState.Active),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Throws_for_hd_wallet()
    {
        var wallet = new ArkWalletInfo("w2", null, null, WalletType.HD, "tr([00000000/86'/1'/0']xpub.../0/*)", 0);
        var walletStorage = Substitute.For<IWalletStorage>();
        walletStorage.GetWalletById("w2", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ArkWalletInfo?>(wallet));
        var sut = new SingleKeyVtxoRecoveryService(
            [], walletStorage, Substitute.For<IContractStorage>(), Substitute.For<IContractService>(),
            Substitute.For<IClientTransport>());

        Assert.ThatAsync(() => sut.DiscoverAsync("w2"), Throws.InvalidOperationException);
    }

    [Test]
    public async Task EnsureDefaultAsync_derives_current_signer_default_even_when_contracts_exist()
    {
        var user = NewKey();
        var wallet = new ArkWalletInfo("w1", null, null, WalletType.SingleKey, Desc(user).ToString(), 0);
        var walletStorage = Substitute.For<IWalletStorage>();
        walletStorage.GetWalletById("w1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkWalletInfo?>(wallet));
        var contractService = Substitute.For<IContractService>();
        var sut = new SingleKeyVtxoRecoveryService(
            [], walletStorage, Substitute.For<IContractStorage>(), contractService, Substitute.For<IClientTransport>());

        await sut.EnsureDefaultAsync("w1");

        await contractService.Received(1).DeriveContract(
            "w1",
            NextContractPurpose.SendToSelf,
            ContractActivityState.Active,
            Arg.Is<Dictionary<string, string>>(m => m["Source"] == "Default"),
            Arg.Any<CancellationToken>());
    }

    private static ArkServerInfo TestServerInfo(ECXOnlyPubKey signer, Dictionary<ECXOnlyPubKey, long> deprecated)
    {
        var emptyMultisig = new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());
        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: Desc(signer),
            DeprecatedSigners: deprecated,
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: signer,
            CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "test-digest");
    }
}

using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Services;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class DelegatingWalletProviderTests
{
    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor UserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    private static readonly OutputDescriptor DelegateKey =
        KeyExtensions.ParseOutputDescriptor(
            "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b",
            Network.RegTest);

    private static readonly Sequence ExitDelay = new(144);

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
        FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0")
    );

    [Test]
    public async Task GetAddressProviderAsync_WrapsInnerProvider()
    {
        var paymentContract = new ArkPaymentContract(ServerKey, ExitDelay, UserKey);
        var entity = paymentContract.ToEntity("wallet-1");

        var innerAddr = Substitute.For<IArkadeAddressProvider>();
        innerAddr.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(ArkContract, ArkContractEntity)>((paymentContract, entity)));

        var innerWallet = Substitute.For<IWalletProvider>();
        innerWallet.GetAddressProviderAsync("wallet-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeAddressProvider?>(innerAddr));

        var delegatorProvider = Substitute.For<IDelegatorProvider>();
        delegatorProvider.GetDelegatorInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DelegatorInfo(
                Convert.ToHexString(DelegateKey.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(),
                "0", "")));

        var clientTransport = Substitute.For<IClientTransport>();
        clientTransport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateServerInfo()));

        var provider = new DelegatingWalletProvider(innerWallet, delegatorProvider, clientTransport);

        var addrProvider = await provider.GetAddressProviderAsync("wallet-1");

        Assert.That(addrProvider, Is.Not.Null);
        Assert.That(addrProvider, Is.InstanceOf<DelegatingAddressProvider>());

        // Verify it produces delegate contracts
        var (contract, _) = await addrProvider!.GetNextContract(NextContractPurpose.Receive, ContractActivityState.Active);
        Assert.That(contract, Is.InstanceOf<ArkDelegateContract>());
    }

    [Test]
    public async Task GetSignerAsync_DelegatesToInner()
    {
        var signer = Substitute.For<IArkadeWalletSigner>();
        var innerWallet = Substitute.For<IWalletProvider>();
        innerWallet.GetSignerAsync("wallet-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeWalletSigner?>(signer));

        var delegatorProvider = Substitute.For<IDelegatorProvider>();
        var clientTransport = Substitute.For<IClientTransport>();

        var provider = new DelegatingWalletProvider(innerWallet, delegatorProvider, clientTransport);

        var result = await provider.GetSignerAsync("wallet-1");

        Assert.That(result, Is.SameAs(signer));
    }

    [Test]
    public async Task GetAddressProviderAsync_ReturnsNull_WhenInnerReturnsNull()
    {
        var innerWallet = Substitute.For<IWalletProvider>();
        innerWallet.GetAddressProviderAsync("unknown", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeAddressProvider?>(null));

        var delegatorProvider = Substitute.For<IDelegatorProvider>();
        var clientTransport = Substitute.For<IClientTransport>();

        var provider = new DelegatingWalletProvider(innerWallet, delegatorProvider, clientTransport);

        var result = await provider.GetAddressProviderAsync("unknown");

        Assert.That(result, Is.Null);
    }
}

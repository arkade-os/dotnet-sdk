using Microsoft.Extensions.Options;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class DelegateeServiceTests
{
    private const string DelegatePubkeyHex =
        "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b";

    private static readonly OutputDescriptor DelegateDescriptor =
        KeyExtensions.ParseOutputDescriptor(DelegatePubkeyHex, Network.RegTest);

    private static DelegateeService CreateSut(DelegatorOptions options)
    {
        var delegatePubkey = ECPubKey.Create(Convert.FromHexString(DelegatePubkeyHex));

        var signer = Substitute.For<IArkadeWalletSigner>();
        signer.GetPubKey(Arg.Any<OutputDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(delegatePubkey));

        var walletProvider = Substitute.For<IWalletProvider>();
        walletProvider.GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeWalletSigner?>(signer));

        return new DelegateeService(walletProvider, new OptionsWrapper<DelegatorOptions>(options));
    }

    [Test]
    public async Task GetInfoAsync_returns_configured_fee_address_and_signer_pubkey()
    {
        var sut = CreateSut(new DelegatorOptions
        {
            WalletId = "delegator",
            DelegateDescriptor = DelegateDescriptor,
            Fee = "100",
            DelegatorAddress = "tark1qexampleaddr"
        });

        var info = await sut.GetInfoAsync();

        Assert.That(info.Pubkey, Is.EqualTo(DelegatePubkeyHex));
        Assert.That(info.Fee, Is.EqualTo("100"));
        Assert.That(info.DelegatorAddress, Is.EqualTo("tark1qexampleaddr"));
    }
}

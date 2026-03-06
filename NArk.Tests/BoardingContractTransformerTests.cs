using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transformers;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class BoardingContractTransformerTests
{
    private IWalletProvider _walletProvider = null!;
    private IArkadeAddressProvider _addressProvider = null!;
    private IArkadeWalletSigner _signer = null!;
    private BoardingContractTransformer _transformer = null!;

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor TestUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    [SetUp]
    public void SetUp()
    {
        _walletProvider = Substitute.For<IWalletProvider>();
        _addressProvider = Substitute.For<IArkadeAddressProvider>();
        _signer = Substitute.For<IArkadeWalletSigner>();

        _walletProvider.GetAddressProviderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeAddressProvider?>(_addressProvider));

        _walletProvider.GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeWalletSigner?>(_signer));

        _addressProvider.IsOurs(Arg.Any<OutputDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        _transformer = new BoardingContractTransformer(_walletProvider);
    }

    private static ArkBoardingContract CreateBoardingContract()
    {
        return new ArkBoardingContract(TestServerKey, new Sequence(144), TestUserKey);
    }

    private static ArkVtxo CreateVtxo(bool swept = false, bool unrolled = false)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Outputs.Add(Money.Satoshis(10000), Script.Empty);
        return new ArkVtxo(
            Script: tx.Outputs[0].ScriptPubKey.ToHex(),
            TransactionId: tx.GetHash().ToString(),
            TransactionOutputIndex: 0,
            Amount: 10000,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: swept,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            ExpiresAtHeight: null,
            Unrolled: unrolled);
    }

    [Test]
    public async Task CanTransform_ReturnsTrueForBoardingContract()
    {
        var contract = CreateBoardingContract();
        var vtxo = CreateVtxo();

        var result = await _transformer.CanTransform("wallet-1", contract, vtxo);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanTransform_ReturnsFalseForPaymentContract()
    {
        var contract = new ArkPaymentContract(TestServerKey, new Sequence(144), TestUserKey);
        var vtxo = CreateVtxo();

        var result = await _transformer.CanTransform("wallet-1", contract, vtxo);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanTransform_ReturnsFalseWhenNotOurs()
    {
        _addressProvider.IsOurs(Arg.Any<OutputDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var contract = CreateBoardingContract();
        var vtxo = CreateVtxo();

        var result = await _transformer.CanTransform("wallet-1", contract, vtxo);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Transform_ProducesCorrectCoin()
    {
        var contract = CreateBoardingContract();
        var vtxo = CreateVtxo(swept: false, unrolled: true);

        var coin = await _transformer.Transform("wallet-1", contract, vtxo);

        Assert.That(coin.Unrolled, Is.True);
        Assert.That(coin.RequiresForfeit(), Is.False);
        Assert.That(coin.Swept, Is.False);
        Assert.That(coin.WalletIdentifier, Is.EqualTo("wallet-1"));
        Assert.That(coin.SpendingScriptBuilder, Is.Not.Null);

        // Verify the spending script matches the collaborative path
        var expectedScript = contract.CollaborativePath().Build();
        Assert.That(coin.SpendingScriptBuilder.Build().Script, Is.EqualTo(expectedScript.Script));
    }

    [Test]
    public async Task Transform_PropagatesVtxoUnrolledFlag()
    {
        var contract = CreateBoardingContract();
        var vtxo = CreateVtxo(swept: false, unrolled: true);

        var coin = await _transformer.Transform("wallet-1", contract, vtxo);

        Assert.That(coin.Unrolled, Is.True);
    }
}

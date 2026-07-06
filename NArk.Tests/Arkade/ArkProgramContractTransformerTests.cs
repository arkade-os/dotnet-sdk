using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Program;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests.Arkade;

[TestFixture]
public class ArkProgramContractTransformerTests
{
    private IWalletProvider _walletProvider = null!;
    private IArkadeAddressProvider _addressProvider = null!;
    private IArkadeWalletSigner _signer = null!;
    private ArkProgramContractTransformer _transformer = null!;

    private static readonly OutputDescriptor TestServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);

    private static readonly OutputDescriptor TestUserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4", Network.RegTest);

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

        _transformer = new ArkProgramContractTransformer(_walletProvider);
    }

    private static ArkVtxo CreateVtxo()
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
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            ExpiresAtHeight: null,
            Unrolled: false);
    }

    private static ArkProgramContract SingleUserFunctionContract(IReadOnlyList<ArkadeToken>? witness = null) =>
        new(TestServerKey,
            new ArkadeProgram
            {
                Version = ArkadeProgram.SupportedVersion,
                Functions = new Dictionary<string, ArkadeFunction>
                {
                    ["claim"] = new()
                    {
                        Tapscript = new ArkadeTapscriptSegment
                        {
                            Signers = [ArkadeToken.FromText("server"), ArkadeToken.FromText("user")],
                            Witness = witness,
                        },
                    },
                },
            },
            new Dictionary<string, ArkadeToken>(),
            user: TestUserKey);

    [Test]
    public async Task CanTransform_ReturnsFalse_ForNonProgramContract()
    {
        var paymentContract = new ArkPaymentContract(TestServerKey, new Sequence(144), TestUserKey);
        var result = await _transformer.CanTransform("wallet-1", paymentContract, CreateVtxo());
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanTransform_ReturnsFalse_WhenNoUserSignablePath()
    {
        var contract = new ArkProgramContract(TestServerKey, new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["exit"] = new() { Tapscript = new ArkadeTapscriptSegment { Signers = [ArkadeToken.FromText("server")] } },
            },
        }, new Dictionary<string, ArkadeToken>());

        var result = await _transformer.CanTransform("wallet-1", contract, CreateVtxo());
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanTransform_ReturnsFalse_WhenUserNotOurs()
    {
        _addressProvider.IsOurs(Arg.Any<OutputDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var result = await _transformer.CanTransform("wallet-1", SingleUserFunctionContract(), CreateVtxo());
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanTransform_ReturnsTrue_ForSingleUserSignablePath()
    {
        var result = await _transformer.CanTransform("wallet-1", SingleUserFunctionContract(), CreateVtxo());
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Transform_ProducesCoin_WithUserSignerAndNoExtraWitness()
    {
        var contract = SingleUserFunctionContract();
        var coin = await _transformer.Transform("wallet-1", contract, CreateVtxo());

        Assert.That(coin.WalletIdentifier, Is.EqualTo("wallet-1"));
        Assert.That(coin.Contract, Is.SameAs(contract));
        Assert.That(coin.SignerDescriptor, Is.EqualTo(TestUserKey));
        Assert.That(coin.SpendingConditionWitness, Is.Null);
    }

    [Test]
    public async Task Transform_ResolvesParamBoundWitness()
    {
        var hash = Convert.FromHexString("4d487dd3753a89bc9fe98401d1196523058251fc");
        var contract = new ArkProgramContract(
            TestServerKey,
            new ArkadeProgram
            {
                Version = ArkadeProgram.SupportedVersion,
                Functions = new Dictionary<string, ArkadeFunction>
                {
                    ["claim"] = new()
                    {
                        Tapscript = new ArkadeTapscriptSegment
                        {
                            Signers = [ArkadeToken.FromText("server"), ArkadeToken.FromText("user")],
                            Witness = [ArkadeToken.FromText("$preimage")],
                        },
                    },
                },
            },
            new Dictionary<string, ArkadeToken> { ["preimage"] = ArkadeToken.FromBytes(hash) },
            user: TestUserKey);

        var coin = await _transformer.Transform("wallet-1", contract, CreateVtxo());

        Assert.That(coin.SpendingConditionWitness, Is.Not.Null);
        Assert.That(coin.SpendingConditionWitness!.PushCount, Is.EqualTo(1));
        Assert.That(coin.SpendingConditionWitness.GetUnsafePush(0), Is.EqualTo(hash));
    }

    [Test]
    public async Task CanTransform_ReturnsFalse_WhenWitnessNeedsUnboundCallArgument()
    {
        // Bare (non-$param) witness name — a call-time input this transformer can't supply.
        var contract = SingleUserFunctionContract(witness: [ArkadeToken.FromText("preimage")]);
        var result = await _transformer.CanTransform("wallet-1", contract, CreateVtxo());
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanTransform_ReturnsFalse_WhenMultipleUserSignablePathsExist()
    {
        var contract = new ArkProgramContract(
            TestServerKey,
            new ArkadeProgram
            {
                Version = ArkadeProgram.SupportedVersion,
                Functions = new Dictionary<string, ArkadeFunction>
                {
                    ["collab"] = new()
                    {
                        Tapscript = new ArkadeTapscriptSegment
                        {
                            Signers = [ArkadeToken.FromText("server"), ArkadeToken.FromText("user")],
                        },
                    },
                    ["unilateral"] = new()
                    {
                        Tapscript = new ArkadeTapscriptSegment
                        {
                            Signers = [ArkadeToken.FromText("user")],
                            Csv = new Sequence(144),
                        },
                    },
                },
            },
            new Dictionary<string, ArkadeToken>(),
            user: TestUserKey);

        var result = await _transformer.CanTransform("wallet-1", contract, CreateVtxo());
        Assert.That(result, Is.False);
    }
}

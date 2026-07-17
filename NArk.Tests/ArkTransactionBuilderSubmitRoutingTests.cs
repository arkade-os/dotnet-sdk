using NArk.Abstractions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Verifies that <see cref="TransactionHelpers.ArkTransactionBuilder.SubmitArkTransaction"/>
/// routes to an <see cref="ISpendSubmitHandler"/> when one engages, and otherwise falls
/// through to the unchanged arkd cooperative submit — i.e. the seam adds a covenant path
/// without regressing normal spends. Uses an empty checkpoint set so the routing decision
/// is exercised in isolation from checkpoint signing.
/// </summary>
[TestFixture]
public class ArkTransactionBuilderSubmitRoutingTests
{
    private IClientTransport _transport = null!;
    private ISpendSubmitHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _transport = Substitute.For<IClientTransport>();
        _transport.SubmitTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SubmitTxResponse("arktxid", "finalarktx", [])));
        _transport.FinalizeTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _handler = Substitute.For<ISpendSubmitHandler>();
    }

    [Test]
    public async Task EngagingHandler_TakesOverSubmit_AndSkipsArkd()
    {
        _handler.ShouldHandle(Arg.Any<IReadOnlyCollection<ArkCoin>>()).Returns(true);

        await Builder().SubmitArkTransaction([], AnyPsbt(), [], CancellationToken.None);

        await _handler.Received(1).SubmitAsync(
            Arg.Any<IReadOnlyCollection<ArkCoin>>(), Arg.Any<PSBT>(),
            Arg.Any<IReadOnlyList<PSBT>>(), Arg.Any<CancellationToken>());
        await _transport.DidNotReceive().SubmitTx(
            Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NonEngagingHandler_FallsThroughToArkd()
    {
        _handler.ShouldHandle(Arg.Any<IReadOnlyCollection<ArkCoin>>()).Returns(false);

        await Builder().SubmitArkTransaction([], AnyPsbt(), [], CancellationToken.None);

        await _transport.Received(1).SubmitTx(
            Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
        await _transport.Received(1).FinalizeTx(
            Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
        await _handler.DidNotReceive().SubmitAsync(
            Arg.Any<IReadOnlyCollection<ArkCoin>>(), Arg.Any<PSBT>(),
            Arg.Any<IReadOnlyList<PSBT>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoHandlersRegistered_FollowsArkdFlowUnchanged()
    {
        var builder = new TransactionHelpers.ArkTransactionBuilder(
            _transport, Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(),
            Substitute.For<IIntentStorage>());

        await builder.SubmitArkTransaction([], AnyPsbt(), [], CancellationToken.None);

        await _transport.Received(1).SubmitTx(
            Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    private TransactionHelpers.ArkTransactionBuilder Builder() =>
        new(_transport, Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(),
            Substitute.For<IIntentStorage>(), submitHandlers: [_handler]);

    private static PSBT AnyPsbt()
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Coins(1), new Script(OpcodeType.OP_TRUE)));
        return PSBT.FromTransaction(tx, Network.RegTest);
    }
}

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
        // Echo back the txid of whatever was submitted, as an honest server does. A fixed string
        // here would fail the check that the finalized transaction is the one we built — which is
        // the point of that check, and worth a fake that behaves like the thing it stands in for.
        _transport.SubmitTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new SubmitTxResponse(
                PSBT.Parse(call.ArgAt<string>(0), Network.RegTest).GetGlobalTransaction().GetHash().ToString(),
                "finalarktx", [])));
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

    [Test]
    public void AServerRenamingTheTransaction_IsRefusedBeforeFinalizing()
    {
        // The response's txid is the server's claim about which transaction our signed checkpoints
        // belong to. Accepting a different one would apply them — and on a claim they carry the
        // preimage — to something we never built.
        _transport.SubmitTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SubmitTxResponse("not-the-txid-we-submitted", "finalarktx", [])));

        var builder = new TransactionHelpers.ArkTransactionBuilder(
            _transport, Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(),
            Substitute.For<IIntentStorage>());

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.SubmitArkTransaction([], AnyPsbt(), [], CancellationToken.None));
    }

    [Test]
    public async Task ARenamedTransaction_IsNeverFinalized()
    {
        _transport.SubmitTx(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SubmitTxResponse("not-the-txid-we-submitted", "finalarktx", [])));

        var builder = new TransactionHelpers.ArkTransactionBuilder(
            _transport, Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(),
            Substitute.For<IIntentStorage>());

        try
        {
            await builder.SubmitArkTransaction([], AnyPsbt(), [], CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The refusal is asserted above; what matters here is what did not happen next.
        }

        await _transport.DidNotReceive().FinalizeTx(
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

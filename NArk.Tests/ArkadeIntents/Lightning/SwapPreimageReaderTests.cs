using NArk.Abstractions.Helpers;
using NArk.ArkadeIntents.Lightning;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Proving a Lightning swap was filled, rather than assuming it from a spend.
/// </summary>
/// <remarks>
/// The covenant's non-interactive refund carries no timelock, so the counterparty can push it at any
/// moment and both leaves leave the same trace: an output that used to be there and now is not. The
/// preimage is the only thing that tells them apart, and it is checkable, so this is the difference
/// between reporting a payment that completed and reporting one whose money came back.
/// </remarks>
[TestFixture]
public class SwapPreimageReaderTests
{
    private static readonly byte[] Preimage =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

    private static string PaymentHash =>
        Convert.ToHexString(NBitcoin.Crypto.Hashes.SHA256(Preimage)).ToLowerInvariant();

    private static readonly OutPoint Lockup = new(uint256.One, 0);

    [Test]
    public async Task APreimageInTheConditionField_IsFound()
    {
        // Where a spend built by this SDK puts it.
        var transport = TransportReturning(SpendOf(Lockup, condition: new WitScript(Op.GetPushOp(Preimage))));

        var found = await SwapPreimageReader.FindAsync(transport, Lockup, "spender", PaymentHash);

        Assert.That(found, Is.EqualTo(Preimage));
    }

    [Test]
    public async Task APreimageInTheFinalWitness_IsFound()
    {
        // Where a spend built by anything else puts it — the counterparty's claim is not our code.
        var transport = TransportReturning(SpendOf(Lockup, final: new WitScript(Op.GetPushOp(Preimage))));

        var found = await SwapPreimageReader.FindAsync(transport, Lockup, "spender", PaymentHash);

        Assert.That(found, Is.EqualTo(Preimage));
    }

    [Test]
    public async Task AWitnessOfTheRightShapeButTheWrongValue_IsRefused()
    {
        // The whole point: a 32-byte push is not evidence, a hash match is. Believing the shape would
        // let anything that spends the lockup pose as a fill.
        var impostor = new byte[32];
        Array.Fill(impostor, (byte)0xab);
        var transport = TransportReturning(SpendOf(Lockup, final: new WitScript(Op.GetPushOp(impostor))));

        Assert.That(await SwapPreimageReader.FindAsync(transport, Lockup, "spender", PaymentHash), Is.Null);
    }

    [Test]
    public async Task ASpendOfSomeOtherOutput_IsIgnored()
    {
        // A transaction can carry inputs that have nothing to do with this swap, and one of them
        // revealing some other swap's preimage must not settle ours.
        var transport = TransportReturning(
            SpendOf(new OutPoint(uint256.Zero, 7), final: new WitScript(Op.GetPushOp(Preimage))));

        Assert.That(await SwapPreimageReader.FindAsync(transport, Lockup, "spender", PaymentHash), Is.Null);
    }

    [Test]
    public async Task ARefundThatRevealsNothing_ProvesNothing()
    {
        var transport = TransportReturning(SpendOf(Lockup));

        Assert.That(await SwapPreimageReader.FindAsync(transport, Lockup, "spender", PaymentHash), Is.Null);
    }

    [Test]
    public async Task AnUnreachableIndexer_ProvesNothingRatherThanThrowing()
    {
        // Read lag and an outage look alike from here, and a caller's only correct response to either
        // is the same: not provably filled. Throwing would stop a reconciliation pass over every
        // other swap for a fact about one.
        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<string>>>(_ => throw new HttpRequestException("indexer down"));

        Assert.That(await SwapPreimageReader.FindAsync(transport, Lockup, "spender", PaymentHash), Is.Null);
    }

    [Test]
    public async Task NoSpendingTransaction_IsNotAsked()
    {
        var transport = Substitute.For<IClientTransport>();

        Assert.That(await SwapPreimageReader.FindAsync(transport, Lockup, "", PaymentHash), Is.Null);

        await transport.DidNotReceive().GetVirtualTxsAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    /// <summary>A PSBT spending <paramref name="prevOut"/>, carrying whatever witness is given.</summary>
    private static string SpendOf(OutPoint prevOut, WitScript? condition = null, WitScript? final = null)
    {
        var tx = Network.Main.CreateTransaction();
        tx.Inputs.Add(new TxIn(prevOut));
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));

        var psbt = PSBT.FromTransaction(tx, Network.Main);
        if (condition is not null) psbt.Inputs[0].SetArkFieldConditionWitness(condition);
        if (final is not null) psbt.Inputs[0].FinalScriptWitness = final;
        return psbt.ToBase64();
    }

    private static IClientTransport TransportReturning(params string[] psbts)
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(psbts));
        return transport;
    }
}

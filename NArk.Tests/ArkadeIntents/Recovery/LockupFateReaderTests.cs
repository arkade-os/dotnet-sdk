using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Recovery;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Recovery;

/// <summary>
/// Deciding from chain data alone what became of a swap lockup.
/// </summary>
/// <remarks>
/// The whole point is that this needs nobody's cooperation. The claim leaf can only be spent by
/// revealing the preimage, and every other leaf returns the money to us — so "spent, but not by a
/// hash-verified claim" is a refund, provable rather than taken on the counterparty's word.
/// </remarks>
[TestFixture]
public class LockupFateReaderTests
{
    private const string Script = "5120" + "aa";
    private const string PaymentHash = "0000000000000000000000000000000000000000000000000000000000000000";

    [Test]
    public async Task NoOutputsAtAll_IsUnknown_NotReturned()
    {
        // Indexer lag and a lockup that was never funded look identical from here. Reading that
        // silence as a refund is how a swap that settled gets reported as one that did not.
        var fate = await Read([]);

        Assert.That(fate.Fate, Is.EqualTo(LockupFate.Unknown));
    }

    [Test]
    public async Task AnUnspentOutput_IsOpen()
    {
        var fate = await Read([Vtxo()]);

        Assert.Multiple(() =>
        {
            Assert.That(fate.Fate, Is.EqualTo(LockupFate.Open));
            Assert.That(fate.IsResolved, Is.False);
        });
    }

    [Test]
    public async Task AnExitedOutput_OutranksOpen()
    {
        // It is unspent, so a naive read calls the swap "still running" — but it sits on-chain under
        // the script now, where no off-chain claim or refund reaches it. A caller told "open" waits
        // for a spend that cannot come.
        var fate = await Read([Vtxo() with { Unrolled = true }, Vtxo(vout: 1)]);

        Assert.Multiple(() =>
        {
            Assert.That(fate.Fate, Is.EqualTo(LockupFate.Exited));
            Assert.That(fate.Stuck, Has.Count.EqualTo(1));
            Assert.That(fate.Stuck!.Single().N, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ASweptOutput_OutranksOpenToo()
    {
        var fate = await Read([Vtxo() with { Swept = true }, Vtxo(vout: 1)]);

        Assert.Multiple(() =>
        {
            Assert.That(fate.Fate, Is.EqualTo(LockupFate.Swept));
            Assert.That(fate.Stuck!.Single().N, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task SpentWithNoProvenPreimage_IsReturned()
    {
        // Every leaf that is not a claim hands the money back: the covenant's non-interactive refund
        // is pinned to our own address, and the rest need our own signature. So this is decidable
        // without asking the counterparty anything.
        var fate = await Read(
            [Vtxo() with { SpentByTransactionId = new string('c', 64) }], spendIsVisible: true);

        Assert.Multiple(() =>
        {
            Assert.That(fate.Fate, Is.EqualTo(LockupFate.Returned));
            Assert.That(fate.IsResolved, Is.True);
            Assert.That(fate.Preimage, Is.Null);
        });
    }

    [Test]
    public async Task ASpendTheIndexerCannotProduce_IsUnknown_NotReturned()
    {
        // The vtxo says spent and the indexer cannot hand over the transaction. That is a gap in
        // what we can see, not a refund — pronouncing `Returned` here reports the money home while
        // it may in fact have been claimed, which is the one direction that must never be guessed.
        // The default substitute returns no transactions, which is exactly this case.
        var fate = await Read([Vtxo() with { SpentByTransactionId = new string('c', 64) }]);

        Assert.That(fate.Fate, Is.EqualTo(LockupFate.Unknown));
    }

    [Test]
    public void AResolvedFate_IsOnlyClaimedOrReturned()
    {
        // The states in which the contract itself was settled, as opposed to a negotiation that
        // ended without one ever being funded.
        Assert.Multiple(() =>
        {
            Assert.That(new LockupFateResult(LockupFate.Claimed, [1]).IsResolved, Is.True);
            Assert.That(new LockupFateResult(LockupFate.Returned).IsResolved, Is.True);
            Assert.That(new LockupFateResult(LockupFate.Open).IsResolved, Is.False);
            Assert.That(new LockupFateResult(LockupFate.Exited).IsResolved, Is.False);
            Assert.That(new LockupFateResult(LockupFate.Swept).IsResolved, Is.False);
            Assert.That(new LockupFateResult(LockupFate.Unknown).IsResolved, Is.False);
        });
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static Task<LockupFateResult> Read(ArkVtxo[] vtxos, bool spendIsVisible = false)
    {
        // ReturnsForAnyArgs, not a named-argument arrangement: GetVtxos carries more parameters than
        // the reader passes, so matching on a subset silently arranges nothing and every case comes
        // back Unknown — which is a plausible-looking answer and would have made this whole fixture
        // pass vacuously.
        var storage = Substitute.For<IVtxoStorage>();
        storage.GetVtxos().ReturnsForAnyArgs(vtxos);

        var transport = Substitute.For<IClientTransport>();
        // An unparseable blob still counts as "the indexer produced something": the preimage search
        // then finds nothing in it, which is the refund reading. Absence of any transaction is the
        // separate case, and the one that must not be read as a verdict.
        transport.GetVirtualTxsAsync(default!, default)
            .ReturnsForAnyArgs(spendIsVisible ? ["not-a-psbt"] : []);

        return LockupFateReader.ReadAsync(transport, storage, Script, PaymentHash);
    }

    private static ArkVtxo Vtxo(uint vout = 0) =>
        new(Script: Script, TransactionId: new string('b', 64), TransactionOutputIndex: vout,
            Amount: 50_000, SpentByTransactionId: null, SettledByTransactionId: null, Swept: false,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: null);
}

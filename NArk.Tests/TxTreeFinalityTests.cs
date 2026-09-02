using NArk.Core.Batches;
using NBitcoin;

namespace NArk.Tests;

/// <summary>
/// The operator builds the VTXO and connector trees, and the client only ever gets to
/// co-sign what it is handed. These tests drive <see cref="TxTree.Validate"/> on trees the
/// operator could offer, and check it refuses the ones Bitcoin would not relay on demand.
/// </summary>
[TestFixture]
public class TxTreeFinalityTests
{
    private static readonly Network Net = Network.RegTest;
    private static readonly Script OutputScript =
        BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Net).ScriptPubKey;

    [Test]
    public void Final_tree_is_accepted()
    {
        Assert.DoesNotThrow(() => Tree().Validate());
    }

    [Test]
    public void Root_with_a_locktime_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Tree(rootLockTime: new LockTime(800_000)).Validate());
        Assert.That(ex!.Message, Does.Contain("locktime"));
    }

    [Test]
    public void Root_with_a_non_final_sequence_is_rejected()
    {
        // A relative timelock on the root holds the whole subtree back until it matures.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Tree(rootSequence: new Sequence(144)).Validate());
        Assert.That(ex!.Message, Does.Contain("sequence"));
    }

    [Test]
    public void Child_with_a_locktime_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Tree(childLockTime: new LockTime(800_000)).Validate());
        Assert.That(ex!.Message, Does.Contain("locktime"));
    }

    [Test]
    public void Child_with_a_non_final_sequence_is_rejected()
    {
        // Every node has to be reachable, not just the one the caller validates.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Tree(childSequence: new Sequence(0xFFFFFFFE)).Validate());
        Assert.That(ex!.Message, Does.Contain("sequence"));
    }

    /// <summary>
    /// A three-generation tree that satisfies every other rule <see cref="TxTree.Validate"/>
    /// applies, so only the finality of the node under test decides the outcome.
    /// </summary>
    private static TxTree Tree(
        LockTime? rootLockTime = null,
        Sequence? rootSequence = null,
        LockTime? childLockTime = null,
        Sequence? childSequence = null)
    {
        var root = Node(new OutPoint(uint256.One, 0),
            [Money.Satoshis(100_000), Money.Satoshis(100_000)], rootLockTime, rootSequence);

        // Built after the parent so each node spends the parent's real txid, which moves
        // whenever a locktime or sequence is changed above.
        var child = Node(new OutPoint(root.GetGlobalTransaction().GetHash(), 0),
            [Money.Satoshis(50_000), Money.Satoshis(50_000)], childLockTime, childSequence);

        var grandchild = Node(new OutPoint(child.GetGlobalTransaction().GetHash(), 0),
            [Money.Satoshis(50_000)]);

        return new TxTree(root, new Dictionary<int, TxTree>
        {
            [0] = new(child, new Dictionary<int, TxTree> { [0] = new(grandchild) })
        });
    }

    /// <summary>A tree node as the operator's builder produces it: version 3 and final.</summary>
    private static PSBT Node(
        OutPoint prevOut,
        Money[] outputs,
        LockTime? lockTime = null,
        Sequence? sequence = null)
    {
        var tx = Transaction.Create(Net);
        tx.Version = 3;
        tx.LockTime = lockTime ?? LockTime.Zero;
        tx.Inputs.Add(new TxIn(prevOut) { Sequence = sequence ?? Sequence.Final });
        foreach (var amount in outputs)
            tx.Outputs.Add(new TxOut(amount, OutputScript));
        return PSBT.FromTransaction(tx, Net);
    }
}

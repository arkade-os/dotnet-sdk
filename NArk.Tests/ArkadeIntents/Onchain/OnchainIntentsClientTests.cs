using NArk.ArkadeIntents.Onchain;

namespace NArk.Tests.ArkadeIntents.Onchain;

/// <summary>
/// <see cref="OnchainIntentsClient.ResolveLockupContract"/>: which of the two VHTLC lockup shapes
/// the solver will actually fund on this corridor's Arkade leg.
/// </summary>
/// <remarks>
/// The client funds this corridor's Arkade lockup itself — same as the Lightning send leg, and
/// unlike the Lightning receive leg — so the safety property mirrors
/// <c>LightningSendGatesTests</c>: accept whichever shape the solver quoted, refuse an address that
/// matches neither. <c>internal</c> rather than <c>private</c> purely so this can be exercised
/// directly, the same seam <c>LightningIntentsClient.SelectRefundable</c> already uses.
/// </remarks>
[TestFixture]
public class OnchainIntentsClientTests
{
    /// <summary>The shape every lockup funded so far carries is still accepted.</summary>
    [Test]
    public void ResolveLockupContract_AcceptsTheEightLeafShapeWhenItMatches()
    {
        var (eightLeaf, nineLeaf) = LockupShapes.Candidates();
        var quoted = eightLeaf.GetArkAddress().ToString(false);

        var resolved = OnchainIntentsClient.ResolveLockupContract(eightLeaf, nineLeaf, quoted, isMainnet: false);

        Assert.That(resolved, Is.SameAs(eightLeaf));
    }

    /// <summary>A solver on the newer shape is no longer refused.</summary>
    [Test]
    public void ResolveLockupContract_AcceptsTheNineLeafShapeWhenItMatches()
    {
        // A solver funding the full nine-leaf suite quotes an address this client would never have
        // derived on its own before — the swap must still be fundable.
        var (eightLeaf, nineLeaf) = LockupShapes.Candidates();
        var quoted = nineLeaf.GetArkAddress().ToString(false);

        var resolved = OnchainIntentsClient.ResolveLockupContract(eightLeaf, nineLeaf, quoted, isMainnet: false);

        Assert.That(resolved, Is.SameAs(nineLeaf));
    }

    /// <summary>An address matching neither derivation is still refused.</summary>
    [Test]
    public void ResolveLockupContract_ThrowsWhenTheQuoteMatchesNeitherShape()
    {
        // The refusal that must never soften: an address matching neither of the client's own
        // derivations must never be accepted, or a wrong or malicious solver could walk the client
        // into funding a script nobody here can rebuild.
        var (eightLeaf, nineLeaf) = LockupShapes.Candidates();

        var ex = Assert.Throws<OnchainSendNotFundableException>(() =>
            OnchainIntentsClient.ResolveLockupContract(
                eightLeaf, nineLeaf, "ark1qsomewhere-else", isMainnet: false));

        Assert.That(ex!.Reason, Is.EqualTo(OnchainSendRefusalReason.IncompleteQuote));
    }

    /// <summary>An absent address is a refusal, not a free pass.</summary>
    [Test]
    public void ResolveLockupContract_ThrowsWhenTheSolverSentNoneAtAll()
    {
        // A missing address must not read as "nothing to compare, carry on" — the same rule
        // AssertMatches already applies to this corridor's L1 HTLC address.
        var (eightLeaf, nineLeaf) = LockupShapes.Candidates();

        Assert.Throws<OnchainSendNotFundableException>(() =>
            OnchainIntentsClient.ResolveLockupContract(eightLeaf, nineLeaf, null, isMainnet: false));
    }
}

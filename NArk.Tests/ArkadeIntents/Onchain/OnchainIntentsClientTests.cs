using System.Security.Cryptography;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Onchain;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

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
    [Test]
    public void ResolveLockupContract_AcceptsTheEightLeafShapeWhenItMatches()
    {
        var (eightLeaf, nineLeaf) = Candidates();
        var quoted = eightLeaf.GetArkAddress().ToString(false);

        var resolved = OnchainIntentsClient.ResolveLockupContract(eightLeaf, nineLeaf, quoted, isMainnet: false);

        Assert.That(resolved, Is.SameAs(eightLeaf));
    }

    [Test]
    public void ResolveLockupContract_AcceptsTheNineLeafShapeWhenItMatches()
    {
        // A solver funding the full nine-leaf suite quotes an address this
        // client would never have derived on its own before — the swap must still be fundable.
        var (eightLeaf, nineLeaf) = Candidates();
        var quoted = nineLeaf.GetArkAddress().ToString(false);

        var resolved = OnchainIntentsClient.ResolveLockupContract(eightLeaf, nineLeaf, quoted, isMainnet: false);

        Assert.That(resolved, Is.SameAs(nineLeaf));
    }

    [Test]
    public void ResolveLockupContract_ThrowsWhenTheQuoteMatchesNeitherShape()
    {
        // The refusal that must never soften: an address matching neither of the client's own
        // derivations must never be accepted, or a wrong or malicious solver could walk the client
        // into funding a script nobody here can rebuild.
        var (eightLeaf, nineLeaf) = Candidates();

        var ex = Assert.Throws<OnchainSendNotFundableException>(() =>
            OnchainIntentsClient.ResolveLockupContract(
                eightLeaf, nineLeaf, "ark1qsomewhere-else", isMainnet: false));

        Assert.That(ex!.Reason, Is.EqualTo(OnchainSendRefusalReason.IncompleteQuote));
    }

    [Test]
    public void ResolveLockupContract_ThrowsWhenTheSolverSentNoneAtAll()
    {
        // A missing address must not read as "nothing to compare, carry on" — the same rule
        // AssertMatches already applies to this corridor's L1 HTLC address.
        var (eightLeaf, nineLeaf) = Candidates();

        var ex = Assert.Throws<OnchainSendNotFundableException>(() =>
            OnchainIntentsClient.ResolveLockupContract(eightLeaf, nineLeaf, null, isMainnet: false));

        Assert.That(ex!.Reason, Is.EqualTo(OnchainSendRefusalReason.IncompleteQuote));
    }

    /// <summary>Two lockup shapes built from one shared, otherwise-arbitrary parameter set.</summary>
    private static (VHTLCv2Contract EightLeaf, VHTLCv2Contract NineLeaf) Candidates() =>
        LightningCorridor.DeriveBothLockupShapes(
            RandomDescriptor(),
            RandomDescriptor(),
            RandomDescriptor(),
            new uint160(RandomNumberGenerator.GetBytes(20), false),
            new LockTime(1_800_600_000),
            new Sequence(TimeSpan.FromSeconds(512)),
            new Sequence(TimeSpan.FromSeconds(512)),
            new Sequence(TimeSpan.FromSeconds(1024)),
            new EmulatorCovenants(RandomXOnly(), RandomP2trPkScript(), RandomP2trPkScript()));

    private static OutputDescriptor RandomDescriptor() =>
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);

    private static ECXOnlyPubKey RandomXOnly() =>
        ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());

    private static byte[] RandomP2trPkScript()
    {
        var script = new byte[34];
        script[0] = 0x51;
        script[1] = 0x20;
        new Key().PubKey.TaprootInternalKey.ToBytes().CopyTo(script, 2);
        return script;
    }
}

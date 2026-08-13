using NArk.Arkade.Emulator;

namespace NArk.Tests.Arkade;

/// <summary>
/// The pinned covenant co-signer keys.
/// </summary>
/// <remarks>
/// These decide who can co-sign a covenant, so a wrong one produces a well-formed contract at an
/// ordinary-looking address that the rest of the network cannot spend — and nothing between here
/// and the counterparty's failed claim detects it. Pinned as literals because the counterparty
/// pins them as literals: if theirs move, this fails here rather than at a funded address.
/// </remarks>
[TestFixture]
public class EmulatorPubKeysTests
{
    [Test]
    public void ThePinnedKeys_MatchTheCounterpartys()
    {
        // Copied from ts-sdk packages/ts-sdk/src/networks.ts.
        Assert.Multiple(() =>
        {
            Assert.That(EmulatorPubKeys.Bitcoin,
                Is.EqualTo("0239c196415da47b26456a101daaa12ba9e445bfe153197f1e2b750bf40e52092e"));
            Assert.That(EmulatorPubKeys.Mutinynet,
                Is.EqualTo("03f823b9b2febc81f4af967e77aed2f541cbd3397c6d8f5a72e32eb7b471af889a"));
            Assert.That(EmulatorPubKeys.Regtest,
                Is.EqualTo("02999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9"));
        });
    }

    [Test]
    public void MutinynetAndRegtest_DoNotShareAKey()
    {
        // The reason this is keyed on the advertised NAME and not on the Bitcoin network: mutinynet
        // resolves to TestNet in this SDK, so keying on the network would make the two networks
        // indistinguishable and serve one of them the other's key.
        Assert.That(EmulatorPubKeys.DefaultFor("mutinynet"),
            Is.Not.EqualTo(EmulatorPubKeys.DefaultFor("regtest")));
    }

    [TestCase("bitcoin")]
    [TestCase("MAINNET")]
    [TestCase("Mutinynet")]
    [TestCase("regtest")]
    public void AKnownNetwork_ResolvesRegardlessOfCase(string name)
    {
        Assert.That(EmulatorPubKeys.DefaultFor(name), Is.Not.Empty);
    }

    [TestCase("signet")]
    [TestCase("testnet")]
    [TestCase("")]
    [TestCase(null)]
    public void AnUnpinnedNetwork_Throws(string? name)
    {
        // No shape-based fallback and no guessing: a wrong key here cannot be detected downstream,
        // so refusing is the only answer that fails where somebody can see it.
        Assert.Throws<InvalidOperationException>(() => EmulatorPubKeys.DefaultFor(name));
    }

    [Test]
    public void AReportedKeyMatchingThePin_Agrees()
    {
        Assert.That(EmulatorPubKeys.AgreesWithPin("mutinynet", EmulatorPubKeys.Mutinynet), Is.True);
    }

    [Test]
    public void AReportedKeyDifferingFromThePin_Disagrees()
    {
        // The case the cross-check exists for: an emulator answering with a key this network is not
        // pinned to. Whether that is a misconfiguration or something standing in the way, it is the
        // last moment before an address is derived from it.
        Assert.That(EmulatorPubKeys.AgreesWithPin("mutinynet", EmulatorPubKeys.Regtest), Is.False);
    }

    [Test]
    public void OnAnUnpinnedNetwork_AnythingAgrees()
    {
        // Nothing to compare against is not the same as a mismatch, and reporting it as one would
        // make the check useless on any deployment running its own emulator.
        Assert.That(EmulatorPubKeys.AgreesWithPin("signet", EmulatorPubKeys.Bitcoin), Is.True);
    }
}

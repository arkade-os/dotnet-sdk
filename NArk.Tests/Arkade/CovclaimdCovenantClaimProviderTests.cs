using NArk.Arkade.Covclaim;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Arkade;

/// <summary>
/// Tests that the provider turns a claim destination into the exact co-signer key the
/// covenant signer expects.
/// </summary>
/// <remarks>
/// This closes the loop on the golden vector: <see cref="VHtlcCovenantClaimTests"/>
/// pins the tapscript leaf built <em>from</em> the tweaked key, and this pins the
/// derivation <em>of</em> that key from the daemon's advertised key. Together they
/// cover the whole path from destination script to funded address, which is the part
/// that fails silently — a wrong key still produces a perfectly valid-looking address
/// that simply no one can ever spend.
/// </remarks>
[TestFixture]
public class CovclaimdCovenantClaimProviderTests
{
    /// <summary>Signer key from the Go vector.</summary>
    private const string EmulatorPubKeyHex =
        "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private const string CovclaimdPubKeyHex =
        "037af11787d87ee1d23ff47b61456d0159572abf1ae6f43ec816a9d605199b0b49";

    private const string ClaimDestinationHex =
        "512079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    private const string ExpectedTweakedKeyHex =
        "77a2e768588b5ced39c389e2ce803041bf9a70d503b34b49edf5970d912dcbb1";

    private static ECPubKey ParsePubKey(string hex)
    {
        Assert.That(
            ECPubKey.TryCreate(Convert.FromHexString(hex), Context.Instance, out _, out var key),
            Is.True, "test vector pubkey should parse");
        return key;
    }

    private static ICovclaimdClient StubClient()
    {
        var client = Substitute.For<ICovclaimdClient>();
        client.GetKeysAsync(Arg.Any<CancellationToken>()).Returns(
            new CovclaimdKeys(ParsePubKey(CovclaimdPubKeyHex), ParsePubKey(EmulatorPubKeyHex)));
        return client;
    }

    [Test]
    public async Task GetCovenantClaimKeyAsync_DerivesTheTweakedSignerKey()
    {
        ICovenantClaimProvider provider = new CovclaimdCovenantClaimProvider(StubClient());

        var key = await provider.GetCovenantClaimKeyAsync(
            new Script(Convert.FromHexString(ClaimDestinationHex)));

        Assert.That(Convert.ToHexString(key.ToBytes()).ToLowerInvariant(),
            Is.EqualTo(ExpectedTweakedKeyHex));
    }

    /// <summary>
    /// The whole safety argument rests on the key being destination-specific, so two
    /// destinations must never yield the same co-signer key.
    /// </summary>
    [Test]
    public async Task GetCovenantClaimKeyAsync_DiffersPerDestination()
    {
        ICovenantClaimProvider provider = new CovclaimdCovenantClaimProvider(StubClient());

        var first = await provider.GetCovenantClaimKeyAsync(
            new Script(Convert.FromHexString(ClaimDestinationHex)));
        var second = await provider.GetCovenantClaimKeyAsync(new Script(Convert.FromHexString(
            "5120c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5")));

        Assert.That(second, Is.Not.EqualTo(first));
    }

    [Test]
    public void GetCovenantClaimKeyAsync_RejectsNonP2trDestination()
    {
        ICovenantClaimProvider provider = new CovclaimdCovenantClaimProvider(StubClient());

        Assert.ThrowsAsync<ArgumentException>(() => provider.GetCovenantClaimKeyAsync(
            new Script(Convert.FromHexString("0014c6047f9441ed7d6d3045406e95c07cd85c778e4b"))));
    }

    [Test]
    public async Task RegisterAsync_ForwardsToTheDaemon()
    {
        var client = StubClient();
        ICovenantClaimProvider provider = new CovclaimdCovenantClaimProvider(client);

        var destination = new Script(Convert.FromHexString(ClaimDestinationHex));
        var preimage = new byte[32];
        var taptree = new[] { new Script(Convert.FromHexString("51")).ToTapScript(TapLeafVersion.C0) };

        await provider.RegisterAsync("tark1qexample", preimage, destination, taptree);

        await client.Received(1).RevealAsync(
            "tark1qexample", preimage, destination, taptree, Arg.Any<CancellationToken>());
    }
}

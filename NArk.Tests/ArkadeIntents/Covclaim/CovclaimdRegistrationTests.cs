using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Covclaim;
using NBitcoin;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NArk.Tests.ArkadeIntents.Covclaim;

/// <summary>
/// The rule that makes a second claimant safe to add: it can fail, and the swap does not.
/// </summary>
/// <remarks>
/// <para>
/// covclaimd races this wallet's own claimer for a funded lockup, and both spend the same covenant
/// leaf pinned to the same payout script — so the worst a broken or hostile daemon achieves is not
/// claiming. That is only true while the registration itself is best-effort: a receive that refused
/// to proceed because the daemon was down would have traded a redundant claimant for no swap at all.
/// </para>
/// <para>
/// The other half is what gets sent. The arkade script comes from the contract rather than being
/// rebuilt at the call site, because the covenant commits to a hash of it — two copies is one more
/// way for them to disagree, and a disagreement here strands the claim instead of failing it.
/// </para>
/// </remarks>
[TestFixture]
public class CovclaimdRegistrationTests
{
    [Test]
    public async Task WithNoDaemonConfigured_NothingIsRegisteredAndNothingFails()
    {
        // The default deployment. A receive is claimed by this wallet alone, exactly as before.
        var registered = await CovclaimdRegistration.TryRegisterAsync(
            client: null, Contract(), "tark1q", new byte[32]);

        Assert.That(registered, Is.False);
    }

    [Test]
    public async Task ADaemonThatIsDown_DoesNotFailTheSwap()
    {
        var client = Substitute.For<ICovclaimdClient>();
        client.RevealAsync(default!, default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new CovclaimdException("unreachable"));

        var registered = await CovclaimdRegistration.TryRegisterAsync(
            client, Contract(), "tark1q", new byte[32]);

        Assert.That(registered, Is.False);
    }

    [Test]
    public async Task AnUnexpectedFailure_IsNotSwallowed()
    {
        // Only the daemon's own failure mode is absorbed. A bug on this side should surface rather
        // than be filed under "covclaimd was unreachable".
        var client = Substitute.For<ICovclaimdClient>();
        client.RevealAsync(default!, default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("bug"));

        Assert.That(() => CovclaimdRegistration.TryRegisterAsync(client, Contract(), "tark1q", new byte[32]),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task ALockupWithNoClaimLeaf_IsNotRegistered()
    {
        // Without that leaf the daemon has no closure it can spend, so a registration would be
        // refused on arrival — and every refusal logged is one an operator has to rule out later.
        var client = Substitute.For<ICovclaimdClient>();

        var registered = await CovclaimdRegistration.TryRegisterAsync(
            client, Contract(withClaimLeaf: false), "tark1q", new byte[32]);

        Assert.Multiple(() =>
        {
            Assert.That(registered, Is.False);
            Assert.That(client.ReceivedCalls(), Is.Empty);
        });
    }

    [Test]
    public async Task TheScriptAndTreeComeFromTheContractItself()
    {
        var client = Substitute.For<ICovclaimdClient>();
        var contract = Contract();

        await CovclaimdRegistration.TryRegisterAsync(client, contract, "tark1qaddress", new byte[32]);

        await client.Received(1).RevealAsync(
            "tark1qaddress",
            Arg.Any<byte[]>(),
            Arg.Is<byte[]>(s => s.SequenceEqual(contract.NonInteractiveClaimArkadeScript)),
            Arg.Is<TapScript[]>(t => t.Length == contract.GetTapScriptList().Length),
            Arg.Any<CancellationToken>());
    }

    private static VHTLCv2Contract Contract(bool withClaimLeaf = true) => new(
        LockupShapes.RandomDescriptor(),
        LockupShapes.RandomDescriptor(),
        LockupShapes.RandomDescriptor(),
        new uint160(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20), false),
        new LockTime(1_800_600_000),
        new Sequence(TimeSpan.FromSeconds(512)),
        new Sequence(TimeSpan.FromSeconds(512)),
        new Sequence(TimeSpan.FromSeconds(1024)),
        withClaimLeaf
            ? new VHTLCv2NonInteractiveClaim(LockupShapes.RandomP2trPkScript(), LockupShapes.RandomXOnly())
            : null);
}

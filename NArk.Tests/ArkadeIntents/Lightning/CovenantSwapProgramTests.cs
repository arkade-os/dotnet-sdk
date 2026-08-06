using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Pins the covenant swap script to the solver side's output, byte for byte.
/// </summary>
/// <remarks>
/// The maker funds the address this script derives. If our compilation drifts from the solver's by
/// a single byte we derive a different address, fund it, and the solver cannot claim it — the
/// deposit then sits until the refund locktime. So these vectors are a safety gate, not a
/// regression test: they were generated from the solver side's own derivation and must never be
/// "fixed" to match ours.
/// </remarks>
[TestFixture]
public class CovenantSwapProgramTests
{
    private static readonly Vectors Fixture = LoadFixture();

    [Test]
    public void Claim_LeafMatchesReferenceImplementation()
    {
        Assert.That(Leaf("claim"), Is.EqualTo(Fixture.Leaves.Claim));
    }

    [Test]
    public void Refund_LeafMatchesReferenceImplementation()
    {
        // Also covers the covenant-tweaked co-signer key the compiler appends after $server:
        // it is derived from the ArkadeScript segment, so a drift in either shows up here.
        Assert.That(Leaf("refund"), Is.EqualTo(Fixture.Leaves.Refund));
    }

    [Test]
    public void UnilateralClaim_LeafMatchesReferenceImplementation()
    {
        Assert.That(Leaf("unilateralClaim"), Is.EqualTo(Fixture.Leaves.UnilateralClaim));
    }

    [Test]
    public void RefundCovenant_PinsThePayoutToTheMakersScript()
    {
        var arkadeScript = Contract().FunctionByName("refund")!.ArkadeScriptBytes;
        Assert.That(Hex(arkadeScript!), Is.EqualTo(Fixture.RefundArkadeScript));
    }

    [Test]
    public void ScriptPubKey_MatchesReferenceImplementation()
    {
        // The address the maker funds. Everything above can agree and this still diverge if the
        // leaves are assembled into the taproot tree in a different order.
        var pkScript = Contract().GetArkAddress().ScriptPubKey.ToBytes();
        Assert.That(Hex(pkScript), Is.EqualTo(Fixture.PkScript));
    }

    [Test]
    public void PreimageHashFromPaymentHash_BridgesSha256ToHash160()
    {
        // ripemd160 of the fixture's sha256(P) — the fixture's own preimageHash.
        var paymentHash = NBitcoin.Crypto.Hashes.SHA256(new byte[32].Select(_ => (byte)7).ToArray());
        Assert.That(
            Hex(CovenantSwapProgram.PreimageHashFromPaymentHash(paymentHash)),
            Is.EqualTo(Fixture.Inputs.PreimageHash));
    }

    [Test]
    public void Build_RejectsALocktimeAVerifierWouldReadAsABlockHeight()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CovenantSwapProgram.Build(Parameters() with { RefundLocktime = 499_999_999 }));
        Assert.That(ex!.Message, Does.Contain("block height"));
    }

    [Test]
    public void Build_RejectsAClaimDelayBip68CannotEncode()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CovenantSwapProgram.Build(Parameters() with { ClaimDelay = 4000 }));
        Assert.That(ex!.Message, Does.Contain("512"));
    }

    [Test]
    public void Build_RejectsARefundDestinationThatIsNotP2tr()
    {
        var notP2tr = Fixture.Inputs.RefundPkScriptBytes;
        notP2tr[0] = 0x00;
        Assert.Throws<ArgumentException>(() =>
            CovenantSwapProgram.Build(Parameters() with { RefundPkScript = notP2tr }));
    }

    [TestCase(32)]
    [TestCase(33)]
    public void BuildContract_AcceptsTheEmulatorKeyInEitherEncoding(int length)
    {
        var xOnly = Fixture.Inputs.EmulatorPubkeyBytes;
        var key = length == 32 ? xOnly : [0x02, .. xOnly];
        var contract = CovenantSwapProgram.BuildContract(
            Parameters() with { EmulatorPubkey = key }, ServerDescriptor());

        // The compressed form carries the same x-only key, so it must derive the same address.
        Assert.That(Hex(contract.GetArkAddress().ScriptPubKey.ToBytes()), Is.EqualTo(Fixture.PkScript));
    }

    private static string Leaf(string function) => Hex(Contract().FunctionByName(function)!.LeafScript);

    private static NArk.Arkade.Contracts.ArkProgramContract Contract() =>
        CovenantSwapProgram.BuildContract(Parameters(), ServerDescriptor());

    private static CovenantSwapParams Parameters() => new(
        Receiver: Fixture.Inputs.ReceiverBytes,
        PreimageHash: Fixture.Inputs.PreimageHashBytes,
        RefundLocktime: Fixture.Inputs.RefundLocktime,
        ClaimDelay: Fixture.Inputs.ClaimDelay,
        EmulatorPubkey: Fixture.Inputs.EmulatorPubkeyBytes,
        RefundPkScript: Fixture.Inputs.RefundPkScriptBytes);

    /// <summary>Wrap the fixture's x-only server key as the taproot descriptor the contract binds <c>$server</c> from.</summary>
    private static OutputDescriptor ServerDescriptor()
    {
        var compressed = Convert.ToHexString([(byte)0x02, .. Fixture.Inputs.ServerBytes]).ToLowerInvariant();
        return KeyExtensions.ParseOutputDescriptor(compressed, Network.RegTest);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static Vectors LoadFixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "ArkadeIntents", "Fixtures", "covenant_swap.json");
        var fixture = JsonSerializer.Deserialize<Vectors>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return fixture ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record Vectors(
        VectorInputs Inputs,
        VectorLeaves Leaves,
        string RefundArkadeScript,
        string PkScript);

    public sealed record VectorInputs(
        string Receiver,
        string Server,
        string EmulatorPubkey,
        string RefundPkScript,
        string PreimageHash,
        uint RefundLocktime,
        uint ClaimDelay)
    {
        public byte[] ReceiverBytes => Convert.FromHexString(Receiver);
        public byte[] ServerBytes => Convert.FromHexString(Server);
        public byte[] EmulatorPubkeyBytes => Convert.FromHexString(EmulatorPubkey);
        public byte[] RefundPkScriptBytes => Convert.FromHexString(RefundPkScript);
        public byte[] PreimageHashBytes => Convert.FromHexString(PreimageHash);
    }

    public sealed record VectorLeaves(string Claim, string Refund, string UnilateralClaim);
}

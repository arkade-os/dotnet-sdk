using System.Text.Json;
using System.Text.Json.Serialization;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Pins the eight-leaf covenant script to the counterparty's own derivation, byte for byte, on both
/// Lightning corridors.
/// </summary>
/// <remarks>
/// Nothing on the wire confirms the address — whichever side funds derives it locally and sends
/// money there. So a single byte of drift produces an address the other side cannot spend, and the
/// funds sit until the refund path opens. These vectors come from the solver's own
/// <c>VHTLC.ScriptV2</c> derivation via <c>Fixtures/generate-covenant-vectors.mjs</c>: if they
/// disagree with this code, this code is what is wrong. Regenerate them after every pull of
/// the reference solver rather than editing them to match.
/// </remarks>
[TestFixture]
public class VHTLCv2ContractTests
{
    private const string SendPair = "arkade:BTC->lightning:BTC";
    private const string ReceivePair = "lightning:BTC->arkade:BTC";

    private static readonly Vectors Fixture = LoadFixture();

    [TestCase(SendPair, "claim")]
    [TestCase(SendPair, "refund")]
    [TestCase(SendPair, "refundWithoutReceiver")]
    [TestCase(SendPair, "unilateralClaim")]
    [TestCase(SendPair, "unilateralRefund")]
    [TestCase(SendPair, "unilateralRefundWithoutReceiver")]
    [TestCase(SendPair, "nonInteractiveClaim")]
    [TestCase(SendPair, "nonInteractiveRefund")]
    [TestCase(ReceivePair, "claim")]
    [TestCase(ReceivePair, "refund")]
    [TestCase(ReceivePair, "refundWithoutReceiver")]
    [TestCase(ReceivePair, "unilateralClaim")]
    [TestCase(ReceivePair, "unilateralRefund")]
    [TestCase(ReceivePair, "unilateralRefundWithoutReceiver")]
    [TestCase(ReceivePair, "nonInteractiveClaim")]
    [TestCase(ReceivePair, "nonInteractiveRefund")]
    public void Leaf_MatchesTheCounterpartysDerivation(string pair, string leaf)
    {
        var contract = Contract(pair);
        var built = leaf switch
        {
            "claim" => contract.CreateClaimScript(),
            "refund" => contract.CreateRefundScript(),
            "refundWithoutReceiver" => contract.CreateRefundWithoutReceiverScript(),
            "unilateralClaim" => contract.CreateUnilateralClaimScript(),
            "unilateralRefund" => contract.CreateUnilateralRefundScript(),
            "unilateralRefundWithoutReceiver" => contract.CreateUnilateralRefundWithoutReceiverScript(),
            "nonInteractiveClaim" => contract.CreateNonInteractiveClaimScript(),
            "nonInteractiveRefund" => contract.CreateNonInteractiveRefundScript(),
            _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf, "unknown leaf"),
        };

        Assert.That(Hex(built.Build().Script.ToBytes()), Is.EqualTo(Fixture.Corridors[pair].Leaves[leaf]));
    }

    [TestCase(SendPair)]
    [TestCase(ReceivePair)]
    public void ScriptPubKey_MatchesTheCounterpartysDerivation(string pair)
    {
        // The one assertion that actually decides whether money moves. Every leaf above can agree
        // and this still diverge, because the merkle root also depends on the order they are
        // assembled in.
        Assert.That(
            Hex(Contract(pair).GetScriptPubKey().ToBytes()),
            Is.EqualTo(Fixture.Corridors[pair].PkScript));
    }

    [TestCase(SendPair)]
    [TestCase(ReceivePair)]
    public void Corridors_DeriveDistinctAddresses(string pair)
    {
        // The two corridors share every input but swap who is sender and who is receiver. If a role
        // were wired positionally-wrong, both would still build and could collide here.
        var other = pair == SendPair ? ReceivePair : SendPair;
        Assert.That(Fixture.Corridors[pair].PkScript, Is.Not.EqualTo(Fixture.Corridors[other].PkScript));
    }

    [Test]
    public void PreimageCondition_GatesTheSizeBeforeHashing()
    {
        // A bare HASH160 lock accepts any preimage that hashes right, including a 20-byte digest of
        // something else. The size gate is what makes the claim leaves specific to the 32-byte
        // secret, and it is the difference between VHTLC.Script and VHTLC.ScriptV2.
        var claim = Fixture.Corridors[SendPair].Leaves["claim"];
        Assert.That(claim, Does.StartWith(Fixture.PreimageCondition));
        Assert.That(Fixture.PreimageCondition, Does.StartWith("82012088"));
    }

    [Test]
    public void EnforcePayTo_CommitsToTheDestinationKeyAlone()
    {
        var corridor = Fixture.Corridors[SendPair];
        Assert.That(
            Hex(VHTLCv2Contract.EnforcePayTo(Convert.FromHexString(corridor.Inputs.NonInteractiveRefundPkScript))),
            Is.EqualTo(corridor.ArkadeScripts["nonInteractiveRefund"]));
    }

    [Test]
    public void Construction_RejectsADestinationThatIsNotP2tr()
    {
        var notP2tr = Convert.FromHexString(Fixture.Corridors[SendPair].Inputs.NonInteractiveClaimPkScript);
        notP2tr[0] = 0x00;
        Assert.Throws<ArgumentException>(() => VHTLCv2Contract.EnforcePayTo(notP2tr));
    }

    private static VHTLCv2Contract Contract(string pair)
    {
        var shared = Fixture.SharedInputs;
        var corridor = Fixture.Corridors[pair];
        return new VHTLCv2Contract(
            ServerDescriptor(),
            Descriptor(corridor.Inputs.Sender),
            Descriptor(corridor.Inputs.Receiver),
            new uint160(Convert.FromHexString(shared.PreimageHash), false),
            new LockTime(shared.RefundLocktime),
            Csv(shared.UnilateralClaimDelay),
            Csv(shared.UnilateralRefundDelay),
            Csv(shared.UnilateralRefundWithoutReceiverDelay),
            // These vectors pin the EIGHT-leaf script the deployed solver funds — the group's
            // legacy shape, not its nine-leaf default.
            new EmulatorCovenants(
                XOnly(shared.EmulatorPubkey),
                Convert.FromHexString(corridor.Inputs.NonInteractiveClaimPkScript),
                Convert.FromHexString(corridor.Inputs.NonInteractiveRefundPkScript),
                EmulatorCovenantsLegacy.PreTimelockedRefund));
    }

    private static Sequence Csv(int seconds) => new(TimeSpan.FromSeconds(seconds));

    private static ECXOnlyPubKey XOnly(string hex) => ECXOnlyPubKey.Create(Convert.FromHexString(hex));

    /// <summary>
    /// Wrap a fixture's x-only key as a descriptor. The parity byte is arbitrary — every leaf
    /// commits to the x-only form — so the even prefix is as good as any.
    /// </summary>
    private static OutputDescriptor Descriptor(string xOnlyHex) =>
        KeyExtensions.ParseOutputDescriptor("02" + xOnlyHex, Network.RegTest);

    /// <summary>Wrap the fixture's x-only server key as the taproot descriptor the contract binds to.</summary>
    private static OutputDescriptor ServerDescriptor()
    {
        return Descriptor(Fixture.SharedInputs.Server);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static Vectors LoadFixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "ArkadeIntents", "Fixtures", "covenant_swap.json");
        return JsonSerializer.Deserialize<Vectors>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record Vectors(
        SharedVectorInputs SharedInputs,
        string PreimageCondition,
        Dictionary<string, CorridorVectors> Corridors);

    public sealed record SharedVectorInputs(
        string Server,
        string EmulatorPubkey,
        string PreimageHash,
        uint RefundLocktime,
        int UnilateralClaimDelay,
        int UnilateralRefundDelay,
        int UnilateralRefundWithoutReceiverDelay);

    public sealed record CorridorVectors(
        CorridorInputs Inputs,
        Dictionary<string, string> Leaves,
        [property: JsonPropertyName("arkadeScripts")] Dictionary<string, string> ArkadeScripts,
        string PkScript);

    public sealed record CorridorInputs(
        string Sender,
        string Receiver,
        string NonInteractiveClaimPkScript,
        string NonInteractiveRefundPkScript);
}

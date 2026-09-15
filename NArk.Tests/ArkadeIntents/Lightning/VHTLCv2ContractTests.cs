using System.Text.Json;
using System.Text.Json.Serialization;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Pins the covenant script to the counterparty's own derivation, byte for byte: the eight-leaf
/// ladder both Lightning corridors build, and every other leaf set <c>VHTLC.Options</c> admits.
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

    // ─── The optional halves of the ladder ───
    //
    // `nonInteractiveClaim` and `nonInteractiveRefund` are independently optional, so the ladder is
    // 6, 7 or 8 leaves — and each count is a different merkle root, hence a different address. The
    // corridors above build the eight-leaf shape, which means they alone would let a hard-coded
    // eight pass.

    [TestCase("no-covenant")]
    [TestCase("claim-only")]
    [TestCase("refund-only")]
    [TestCase("asset")]
    [TestCase("strict-sats")]
    [TestCase("strict-asset")]
    public void Variant_ScriptPubKey_MatchesTheCounterpartysDerivation(string variant)
    {
        Assert.That(
            Hex(VariantContract(variant).GetScriptPubKey().ToBytes()),
            Is.EqualTo(Fixture.Variants[variant].PkScript));
    }

    [TestCase("no-covenant")]
    [TestCase("claim-only")]
    [TestCase("refund-only")]
    [TestCase("asset")]
    [TestCase("strict-sats")]
    [TestCase("strict-asset")]
    public void Variant_EveryLeafItCarries_MatchesTheCounterpartysDerivation(string variant)
    {
        var contract = VariantContract(variant);
        var expected = Fixture.Variants[variant];

        Assert.Multiple(() =>
        {
            foreach (var (leaf, script) in expected.Leaves)
            {
                Assert.That(Hex(Leaf(contract, leaf).Build().Script.ToBytes()), Is.EqualTo(script), leaf);
            }
            // The vector's own count, not ours: a leaf we build and it does not is a leaf that moves
            // the merkle root, and asserting only the ones it names would never see it.
            Assert.That(expected.Leaves, Has.Count.EqualTo(expected.LeafCount));
        });
    }

    [TestCase("no-covenant")]
    [TestCase("claim-only")]
    [TestCase("refund-only")]
    [TestCase("asset")]
    [TestCase("strict-sats")]
    [TestCase("strict-asset")]
    public void Variant_CovenantScripts_MatchTheCounterpartysDerivation(string variant)
    {
        // The bytes the emulator actually executes before it will co-sign. They also decide the
        // co-signer key, so they are already implied by the pkScript above — asserted separately
        // because a covenant that drifts is worth naming as a covenant, not as an address.
        var contract = VariantContract(variant);
        var expected = Fixture.Variants[variant].ArkadeScripts;

        Assert.Multiple(() =>
        {
            if (expected.TryGetValue("nonInteractiveClaim", out var claim))
            {
                Assert.That(Hex(contract.NonInteractiveClaimArkadeScript), Is.EqualTo(claim));
            }
            if (expected.TryGetValue("nonInteractiveRefund", out var refund))
            {
                Assert.That(Hex(contract.NonInteractiveRefundArkadeScript), Is.EqualTo(refund));
            }
        });
    }

    [Test]
    public void TheRefundCovenantLeaf_CommitsToTheTweakedEmulatorKey()
    {
        // The leaf's co-signer is not the emulator's own key but that key tweaked by the covenant,
        // which is what makes its signature conditional on the spend honouring the covenant.
        var contract = VariantContract("refund-only");
        var cosigner = Hex(
            VHTLCv2Contract.CovenantKey(
                XOnly(Fixture.SharedInputs.EmulatorPubkey),
                contract.NonInteractiveRefundArkadeScript).ToBytes());

        Assert.Multiple(() =>
        {
            Assert.That(Fixture.Variants["refund-only"].Leaves["nonInteractiveRefund"],
                Does.Contain(cosigner));
            Assert.That(cosigner, Is.Not.EqualTo(Fixture.SharedInputs.EmulatorPubkey));
        });
    }

    [Test]
    public void ALeafTheContractDoesNotCarry_IsRefusedRatherThanBuilt()
    {
        var contract = VariantContract("no-covenant");

        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => contract.CreateNonInteractiveClaimScript());
            Assert.Throws<InvalidOperationException>(() => contract.CreateNonInteractiveRefundScript());
            // ...and the accessors for what those leaves commit to, which are the same question
            // asked of the covenant rather than of the leaf.
            Assert.Throws<InvalidOperationException>(() => _ = contract.NonInteractiveClaimArkadeScript);
            Assert.Throws<InvalidOperationException>(() => _ = contract.NonInteractiveRefundArkadeScript);
        });
    }

    [Test]
    public void AnAssetNoLeafWouldBind_IsRefused()
    {
        // A sat-only contract that says it carries an asset is the dangerous shape: it derives an
        // address the caller funds believing the asset is protected, and any spend satisfying the
        // sat covenant walks off with it.
        var ex = Assert.Throws<ArgumentException>(
            () => BuildVariant(null, null, VariantAsset()));

        Assert.That(ex!.Message, Does.Contain("nonInteractiveClaim"));
    }

    [Test]
    public void AStrictBoundOnTheSatsAlone_IsRefusedForAnAssetContract()
    {
        // Enforcement the caller asked for, landing on the carrier rather than on the asset that is
        // the actual amount.
        var claim = new VHTLCv2NonInteractiveClaim(
            Convert.FromHexString(Fixture.VariantInputs.NonInteractiveClaimPkScript),
            XOnly(Fixture.SharedInputs.EmulatorPubkey),
            new VHTLCv2StrictClaim(Fixture.VariantInputs.StrictAmount));

        var ex = Assert.Throws<ArgumentException>(() => BuildVariant(claim, null, VariantAsset()));

        Assert.That(ex!.Message, Does.Contain("asset amount"));
    }

    [Test]
    public void AZeroStrictBound_IsRefused()
    {
        // `out >= 0` holds for every output, so it would compile a bound that reads like enforcement
        // and enforces nothing.
        var claim = new VHTLCv2NonInteractiveClaim(
            Convert.FromHexString(Fixture.VariantInputs.NonInteractiveClaimPkScript),
            XOnly(Fixture.SharedInputs.EmulatorPubkey),
            new VHTLCv2StrictClaim(0));

        Assert.Throws<ArgumentException>(() => BuildVariant(claim, null, null));
    }

    private static ScriptBuilder Leaf(VHTLCv2Contract contract, string leaf) => leaf switch
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

    /// <summary>The options each named variant was generated with — see the generator's own table.</summary>
    private static VHTLCv2Contract VariantContract(string variant)
    {
        var inputs = Fixture.VariantInputs;
        var emulator = XOnly(Fixture.SharedInputs.EmulatorPubkey);
        var claimPkScript = Convert.FromHexString(inputs.NonInteractiveClaimPkScript);
        var refundPkScript = Convert.FromHexString(inputs.NonInteractiveRefundPkScript);

        VHTLCv2NonInteractiveClaim Claim(VHTLCv2StrictClaim? strict = null) =>
            new(claimPkScript, emulator, strict);
        VHTLCv2NonInteractiveRefund Refund() => new(refundPkScript, emulator);

        return variant switch
        {
            "no-covenant" => BuildVariant(null, null, null),
            "claim-only" => BuildVariant(Claim(), null, null),
            "refund-only" => BuildVariant(null, Refund(), null),
            "asset" => BuildVariant(Claim(), Refund(), VariantAsset()),
            "strict-sats" => BuildVariant(
                Claim(new VHTLCv2StrictClaim(inputs.StrictAmount)), null, null),
            "strict-asset" => BuildVariant(
                Claim(new VHTLCv2StrictClaim(inputs.StrictAmount, inputs.StrictAssetAmount)),
                Refund(),
                VariantAsset()),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "unknown variant"),
        };
    }

    private static VHTLCv2Asset VariantAsset() =>
        new(Convert.FromHexString(Fixture.VariantInputs.AssetTxid), Fixture.VariantInputs.AssetGroupIndex);

    /// <summary>Every variant shares the send corridor's roles, so only the covenant options vary.</summary>
    private static VHTLCv2Contract BuildVariant(
        VHTLCv2NonInteractiveClaim? claim, VHTLCv2NonInteractiveRefund? refund, VHTLCv2Asset? asset)
    {
        var shared = Fixture.SharedInputs;
        var inputs = Fixture.VariantInputs;
        return new VHTLCv2Contract(
            ServerDescriptor(),
            Descriptor(inputs.Sender),
            Descriptor(inputs.Receiver),
            new uint160(Convert.FromHexString(shared.PreimageHash), false),
            new LockTime(shared.RefundLocktime),
            Csv(shared.UnilateralClaimDelay),
            Csv(shared.UnilateralRefundDelay),
            Csv(shared.UnilateralRefundWithoutReceiverDelay),
            claim,
            refund,
            asset);
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
            new VHTLCv2NonInteractiveClaim(
                Convert.FromHexString(corridor.Inputs.NonInteractiveClaimPkScript),
                XOnly(shared.EmulatorPubkey)),
            new VHTLCv2NonInteractiveRefund(
                Convert.FromHexString(corridor.Inputs.NonInteractiveRefundPkScript),
                XOnly(shared.EmulatorPubkey)));
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
        Dictionary<string, CorridorVectors> Corridors,
        VariantInputs VariantInputs,
        Dictionary<string, VariantVectors> Variants);

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

    public sealed record VariantVectors(
        int LeafCount,
        Dictionary<string, string> Leaves,
        [property: JsonPropertyName("arkadeScripts")] Dictionary<string, string> ArkadeScripts,
        string PkScript);

    public sealed record VariantInputs(
        string Sender,
        string Receiver,
        string NonInteractiveClaimPkScript,
        string NonInteractiveRefundPkScript,
        string AssetTxid,
        int AssetGroupIndex,
        long StrictAmount,
        long StrictAssetAmount);
}

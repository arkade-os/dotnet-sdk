using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NBitcoin;
using NBitcoin.Crypto;

namespace NArk.Tests;

/// <summary>
/// Tests for <see cref="ArkTxWeightEstimator"/> using deterministic keys so expected
/// values can be derived from first principles and cross-checked against the arkd
/// implementation.
///
/// Weight unit formula (BIP-141):
///   WU = non-witness-bytes × 4 + witness-bytes × 1
///
/// Proof-tx structure (mirrors arkd's FinalizeAndExtract in intent/proof.go):
///   inputs = N VTXO inputs + 1 synthetic "toSpend" input identical to inputs[0]
///   → total input count = N+1
/// </summary>
[TestFixture]
public class ArkTxWeightEstimatorTests
{
    private const string ServerHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string UserHex   = "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4";
    private const string User2Hex  = "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b";

    private static readonly Network Net = Network.RegTest;

    private static ArkPaymentContract MakePaymentContract(string userHex = UserHex) =>
        new(
            KeyExtensions.ParseOutputDescriptor(ServerHex, Net),
            new Sequence(144),
            KeyExtensions.ParseOutputDescriptor(userHex, Net));

    private static ArkCoin MakeCoin(ArkPaymentContract contract, ScriptBuilder spendingScript, WitScript? conditionWitness = null)
    {
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(10_000), contract.GetScriptPubKey());
        var needsSequence = spendingScript.BuildScript().Contains(OpcodeType.OP_CHECKSEQUENCEVERIFY);
        return new ArkCoin(
            "wallet", contract,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null,
            outpoint, txOut,
            signerDescriptor: null,
            spendingScriptBuilder: spendingScript,
            spendingConditionWitness: conditionWitness,
            lockTime: null,
            sequence: needsSequence ? new Sequence(144) : null,
            swept: false, unrolled: true);
    }

    // ── GetOutputWeightUnits ──────────────────────────────────────────────────

    [Test]
    public void GetOutputWeightUnits_P2TrOutput_MatchesConstant()
    {
        // P2TR scriptPubKey is always 34 bytes: OP_1(1) + PUSH32(1) + key(32)
        // (8 + 1 + 34) × 4 = 172 WU
        var script = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        Assert.That(script.ToBytes().Length, Is.EqualTo(34), "P2TR script must be 34 bytes");
        var output = new TxOut(Money.Satoshis(546), script);
        Assert.That(ArkTxWeightEstimator.GetOutputWeightUnits(output), Is.EqualTo(ArkTxWeightEstimator.P2TrOutputWu));
        Assert.That(ArkTxWeightEstimator.GetOutputWeightUnits(output), Is.EqualTo(172));
    }

    [Test]
    public void GetOutputWeightUnits_P2WpkhOutput_124Wu()
    {
        // P2WPKH scriptPubKey is 22 bytes: OP_0(1) + PUSH20(1) + hash(20)
        // (8 + 1 + 22) × 4 = 124 WU
        var script = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
        Assert.That(script.ToBytes().Length, Is.EqualTo(22), "P2WPKH script must be 22 bytes");
        var output = new TxOut(Money.Satoshis(546), script);
        Assert.That(ArkTxWeightEstimator.GetOutputWeightUnits(output), Is.EqualTo(124));
    }

    [Test]
    public void GetOutputWeightUnits_OpReturn_GrowsWithDataLength()
    {
        // OP_RETURN(1) + OP_PUSHDATA(1) + data — all non-witness, so ×4
        // data=1:  (8 + 1 + 3)  × 4 = 48 WU
        // data=32: (8 + 1 + 34) × 4 = 172 WU
        // data=75: (8 + 1 + 77) × 4 = 344 WU  — direct push, 1-byte length prefix
        var sizes = new[] { 1, 32, 75 };
        foreach (var dataLen in sizes)
        {
            var data = new byte[dataLen];
            var script = TxNullDataTemplate.Instance.GenerateScriptPubKey(data);
            var scriptLen = script.ToBytes().Length;
            var expected = (8 + 1 + scriptLen) * 4;
            Assert.That(ArkTxWeightEstimator.GetOutputWeightUnits(new TxOut(Money.Zero, script)),
                Is.EqualTo(expected), $"data={dataLen}");
        }
    }

    [Test]
    public void GetOutputWeightUnits_Formula_IsNonWitnessBytesTimesFour()
    {
        // Every output is purely non-witness, so weight = serialised_output_size × 4.
        // Serialised size = value(8) + VarInt(scriptLen) + scriptLen.
        var script = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var output = new TxOut(Money.Satoshis(1), script);
        var scriptLen = script.ToBytes().Length;
        var expected = (8 + 1 + scriptLen) * 4; // VarInt(34) = 1 byte
        Assert.That(ArkTxWeightEstimator.GetOutputWeightUnits(output), Is.EqualTo(expected));
    }

    // ── GetInputWeightUnits — ArkPaymentContract ──────────────────────────────

    /// <summary>
    /// 430 WU is the empirically verified value for a standard collaborative-path
    /// ArkPaymentContract input, derived by comparing arkd's proof-tx weight limit
    /// (40 000 WU) against the observed maximum of 91 inputs per intent:
    ///   (40 000 − 42 − 430 − 172) / 430 = 91  ← exact match
    /// </summary>
    [Test]
    public void GetInputWeightUnits_CollaborativePath_Is430Wu()
    {
        var contract = MakePaymentContract();
        var coin = MakeCoin(contract, contract.CollaborativePath());
        Assert.That(ArkTxWeightEstimator.GetInputWeightUnits(coin), Is.EqualTo(430));
    }

    [Test]
    public void GetInputWeightUnits_CollaborativePath_MatchesManualFormula()
    {
        var contract = MakePaymentContract();
        var coin = MakeCoin(contract, contract.CollaborativePath());

        var spendInfo = contract.GetTaprootSpendInfo();
        var cbLen    = spendInfo.GetControlBlock(coin.SpendingScript).ToBytes().Length;
        var scriptLen = coin.SpendingScript.Script.ToBytes().Length;
        // Collaborative path: [user] OP_CHECKSIGVERIFY [server] OP_CHECKSIG → 2 sigs
        var sigCount = 2;

        var expectedWitnessWu = 1 + sigCount * (1 + 64) + 1 + scriptLen + 1 + cbLen;
        var expected = 41 * 4 + expectedWitnessWu;
        Assert.That(ArkTxWeightEstimator.GetInputWeightUnits(coin), Is.EqualTo(expected));
    }

    [Test]
    public void GetInputWeightUnits_CollaborativePath_ScriptIs68Bytes_CbIs65Bytes()
    {
        // Sanity-check the tree geometry that underpins the 430 WU value.
        // ArkPaymentContract has exactly 2 leaves (collab + unilateral) at depth 1
        // → control block = version(1) + internal_key(32) + sibling_hash(32) = 65 bytes
        // Collab script = [user(32)] CHECKSIGVERIFY [server(32)] CHECKSIG = 68 bytes
        var contract = MakePaymentContract();
        var coin = MakeCoin(contract, contract.CollaborativePath());

        var spendInfo = contract.GetTaprootSpendInfo();
        var cbLen    = spendInfo.GetControlBlock(coin.SpendingScript).ToBytes().Length;
        var scriptLen = coin.SpendingScript.Script.ToBytes().Length;

        Assert.That(scriptLen, Is.EqualTo(68), "collaborative script length");
        Assert.That(cbLen, Is.EqualTo(65), "control block length for 2-leaf tree at depth 1");
    }

    [Test]
    public void GetInputWeightUnits_UnilateralPath_HasOneSig()
    {
        // Unilateral path: [seq] CSV DROP [user] OP_CHECKSIG → 1 sig
        var contract = MakePaymentContract();
        var coin = MakeCoin(contract, contract.UnilateralPath());

        var spendInfo = contract.GetTaprootSpendInfo();
        var cbLen    = spendInfo.GetControlBlock(coin.SpendingScript).ToBytes().Length;
        var scriptLen = coin.SpendingScript.Script.ToBytes().Length;
        var sigCount = 1;

        var expectedWitnessWu = 1 + sigCount * (1 + 64) + 1 + scriptLen + 1 + cbLen;
        var expected = 41 * 4 + expectedWitnessWu;
        Assert.That(ArkTxWeightEstimator.GetInputWeightUnits(coin), Is.EqualTo(expected));
    }

    [Test]
    public void GetInputWeightUnits_UnilateralPath_LighterThanCollaborative()
    {
        // Unilateral has 1 sig vs 2 for collaborative → must be cheaper.
        var contract = MakePaymentContract();
        var collab = MakeCoin(contract, contract.CollaborativePath());
        var unilateral = MakeCoin(contract, contract.UnilateralPath());

        Assert.That(ArkTxWeightEstimator.GetInputWeightUnits(unilateral),
            Is.LessThan(ArkTxWeightEstimator.GetInputWeightUnits(collab)));
    }

    [Test]
    public void GetInputWeightUnits_SpendingConditionWitness_AddsLenPlusDataBytes()
    {
        // A condition witness (e.g. hash-lock preimage) is pushed before the script
        // signatures in the witness stack. WitScript.ToBytes() = [count][len+item...];
        // we subtract the count byte because it's already folded into our item_count.
        var preimage = new byte[32];
        var contract = MakePaymentContract();

        var withoutCondition = MakeCoin(contract, contract.CollaborativePath(), conditionWitness: null);
        var withCondition    = MakeCoin(contract, contract.CollaborativePath(),
            conditionWitness: new WitScript(Op.GetPushOp(preimage)));

        var witScript = new WitScript(Op.GetPushOp(preimage));
        var expectedDelta = witScript.ToBytes().Length - 1; // 34 - 1 = 33 for 32-byte preimage

        Assert.That(expectedDelta, Is.EqualTo(33));
        Assert.That(
            ArkTxWeightEstimator.GetInputWeightUnits(withCondition) -
            ArkTxWeightEstimator.GetInputWeightUnits(withoutCondition),
            Is.EqualTo(expectedDelta));
    }

    // ── GetInputWeightUnits — VHTLC ───────────────────────────────────────────

    [Test]
    public void GetInputWeightUnits_VhtlcClaimPath_MatchesManualFormula()
    {
        // Claim path: OP_HASH160 <hash> OP_EQUAL OP_VERIFY [receiver] CHECKSIGVERIFY [server] CHECKSIG
        // → 2 sigs + 32-byte preimage as SpendingConditionWitness
        var hash = new uint160(Hashes.Hash160(new byte[32]).ToBytes(false));
        var contract = new VHTLCContract(
            KeyExtensions.ParseOutputDescriptor(ServerHex, Net),
            KeyExtensions.ParseOutputDescriptor(UserHex, Net),
            KeyExtensions.ParseOutputDescriptor(User2Hex, Net),
            hash, new LockTime(265),
            new Sequence(17), new Sequence(144), new Sequence(144));

        var preimage = new byte[32];
        var claimScript = contract.CreateClaimScript();
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(10_000), contract.GetScriptPubKey());
        var coin = new ArkCoin(
            "wallet", contract,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null,
            outpoint, txOut,
            signerDescriptor: null,
            spendingScriptBuilder: claimScript,
            spendingConditionWitness: new WitScript(Op.GetPushOp(preimage)),
            lockTime: null, sequence: null,
            swept: false, unrolled: true);

        var spendInfo = contract.GetTaprootSpendInfo();
        var cbLen    = spendInfo.GetControlBlock(coin.SpendingScript).ToBytes().Length;
        var scriptLen = coin.SpendingScript.Script.ToBytes().Length;
        var condWitnessBytes = coin.SpendingConditionWitness!.ToBytes().Length - 1; // 33
        var sigCount = 2; // CHECKSIGVERIFY + CHECKSIG

        var expectedWitnessWu = 1 + sigCount * (1 + 64) + 1 + scriptLen + 1 + cbLen + condWitnessBytes;
        var expected = 41 * 4 + expectedWitnessWu;
        Assert.That(ArkTxWeightEstimator.GetInputWeightUnits(coin), Is.EqualTo(expected));
    }

    [Test]
    public void GetInputWeightUnits_VhtlcCooperativePath_ThreeSigs()
    {
        // Cooperative path: [sender] CHECKSIGVERIFY [receiver] CHECKSIGVERIFY [server] CHECKSIG
        // → 3 sigs, no condition witness
        var hash = new uint160(Hashes.Hash160(new byte[32]).ToBytes(false));
        var contract = new VHTLCContract(
            KeyExtensions.ParseOutputDescriptor(ServerHex, Net),
            KeyExtensions.ParseOutputDescriptor(UserHex, Net),
            KeyExtensions.ParseOutputDescriptor(User2Hex, Net),
            hash, new LockTime(265),
            new Sequence(17), new Sequence(144), new Sequence(144));

        var coopScript = contract.CreateCooperativeScript();
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(10_000), contract.GetScriptPubKey());
        var coin = new ArkCoin(
            "wallet", contract,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null,
            outpoint, txOut,
            signerDescriptor: null,
            spendingScriptBuilder: coopScript,
            spendingConditionWitness: null,
            lockTime: null, sequence: null,
            swept: false, unrolled: true);

        var spendInfo = contract.GetTaprootSpendInfo();
        var cbLen    = spendInfo.GetControlBlock(coin.SpendingScript).ToBytes().Length;
        var scriptLen = coin.SpendingScript.Script.ToBytes().Length;
        var sigCount = 3; // sender CHECKSIGVERIFY + receiver CHECKSIGVERIFY + server CHECKSIG

        var expectedWitnessWu = 1 + sigCount * (1 + 64) + 1 + scriptLen + 1 + cbLen;
        var expected = 41 * 4 + expectedWitnessWu;
        Assert.That(ArkTxWeightEstimator.GetInputWeightUnits(coin), Is.EqualTo(expected));
    }

    // ── Proof-tx implicit arkd test vectors ───────────────────────────────────

    /// <summary>
    /// Empirical vectors: arkd rejects intents whose proof tx exceeds 40 000 WU.
    /// With a standard collaborative-path input (430 WU) and the proof-tx toSpend
    /// extra input, 91 inputs fit under the limit and 92 do not.
    ///
    /// Proof-tx WU = BaseTxWu + toSpendWu + Σ(inputWus) + P2TrOutputWu
    ///             = 42 + 430 + N×430 + 172 = (N+1)×430 + 214
    ///   N=91 → 92×430 + 214 = 39 560 + 214 = 39 774 WU  < 40 000 ✓
    ///   N=92 → 93×430 + 214 = 39 990 + 214 = 40 204 WU  > 40 000 ✓
    /// </summary>
    [Test]
    public void ProofTxWeight_91CollaborativeInputs_Under40000Wu()
    {
        const int n = 91;
        var contract = MakePaymentContract();
        var inputWu = ArkTxWeightEstimator.GetInputWeightUnits(
            MakeCoin(contract, contract.CollaborativePath()));

        var proofTxWu = ArkTxWeightEstimator.BaseTxWu
            + inputWu        // toSpend (mirrors first input)
            + n * inputWu    // N VTXO inputs
            + ArkTxWeightEstimator.P2TrOutputWu;

        Assert.That(proofTxWu, Is.EqualTo(39_774));
        Assert.That(proofTxWu, Is.LessThan(40_000));
    }

    [Test]
    public void ProofTxWeight_92CollaborativeInputs_Over40000Wu()
    {
        const int n = 92;
        var contract = MakePaymentContract();
        var inputWu = ArkTxWeightEstimator.GetInputWeightUnits(
            MakeCoin(contract, contract.CollaborativePath()));

        var proofTxWu = ArkTxWeightEstimator.BaseTxWu
            + inputWu
            + n * inputWu
            + ArkTxWeightEstimator.P2TrOutputWu;

        Assert.That(proofTxWu, Is.EqualTo(40_204));
        Assert.That(proofTxWu, Is.GreaterThan(40_000));
    }

    [Test]
    public void ProofTxWeight_DifferentKeysDontChangeInputWu()
    {
        // Input weight depends on script structure, not key values — keys are always 32 bytes.
        var contract1 = MakePaymentContract(UserHex);
        var contract2 = MakePaymentContract(User2Hex);
        var wu1 = ArkTxWeightEstimator.GetInputWeightUnits(MakeCoin(contract1, contract1.CollaborativePath()));
        var wu2 = ArkTxWeightEstimator.GetInputWeightUnits(MakeCoin(contract2, contract2.CollaborativePath()));
        Assert.That(wu1, Is.EqualTo(wu2));
    }
}

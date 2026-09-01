using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Reproduces one real quote from a live solver, captured off a regtest run.
/// </summary>
/// <remarks>
/// The generated vectors already pin the script construction, but every input there is synthetic.
/// This one is a transcript: real arkd signer key, real emulator key, real operator delay, and the
/// solver's own answer. It is the same assertion the client makes before funding — "is the address
/// you quoted the address I derive?" — with the difference that a failure here would have meant
/// funding a script the solver could not spend.
/// </remarks>
[TestFixture]
[Ignore("Captured before lightning-swap-service#81 (c904d44) re-spaced the unilateral ladder, so "
        + "the quoted address no longer matches what either side derives. It has to be RE-CAPTURED "
        + "from a solver running the new ladder — recomputing it from our own code would turn a "
        + "transcript into a tautology, which is the one thing this test exists not to be.")]
public class LiveQuoteDerivationTests
{
    // Captured 2026-08-09 from `POST /v1/swap` against a live reference solver on the
    // arkade-regtest stack.
    private const string ArkdSigner = "02e35799157be4b37565bb5afe4d04e6a0fa0a4b6a4f4e48b0d904685d253cdbdb";
    private const string EmulatorSigner = "02999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9";
    private const string SolverPubkey = "df5e3a677c20ff3af3c1701e5ed75aa7cc1e3ff8069ea4a8df5012494d7af6eb";
    private const string ClientRefundPubkey = "7c2a5ee7f0d4f5f61b0b6b1d4c9a83a0e2f5c6d7889a0b1c2d3e4f5061728394";
    private const string PaymentHash = "ea7ad684b1ae3975cbbdab9512cd042ccbb5218636d6998262c41ed31693ecd9";
    private const string ReceiverPkScript = "5120ec250c5be12707c56bee7a263fb1495e0fbdb733c9eb35a53b5e57e1e2ec2534";
    private const string RefundPkScript = "5120ec250c5be12707c56bee7a263fb1495e0fbdb733c9eb35a53b5e57e1e2ec2534";
    private const uint RefundLocktime = 1786552072;

    /// <summary>The operator reported 512s, and both sides derive the ladder from it alone.</summary>
    private const uint OperatorExitDelay = 512;

    private const string QuotedLockupAddress =
        "tark1qr340xg400jtxat9hdd0ungyu6s05zjtdf85uj9smyzxshf98ndah54mha3rrh93sjaq85mlzhdxadnsw7rqk2cq0tswgk7slw07njfzt6aw80";

    [Test]
    public void OurDerivation_ReproducesTheAddressTheSolverQuoted()
    {
        var delays = LightningCorridor.UnilateralDelays(TestServerInfo.WithSeconds(OperatorExitDelay));

        var contract = new VHTLCv2Contract(
            Descriptor(ArkdSigner),
            // Positional: on this corridor the client sends and the solver receives.
            sender: Descriptor("02" + ClientRefundPubkey),
            receiver: Descriptor("02" + SolverPubkey),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(PaymentHash)), false),
            new LockTime(RefundLocktime),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            new NonInteractiveParameters(
                ECXOnlyPubKey.Create(Convert.FromHexString(EmulatorSigner)[1..]),
                receiverPkScript: Convert.FromHexString(ReceiverPkScript),
                senderPkScript: Convert.FromHexString(RefundPkScript),
                // The solver that quoted these addresses funded the pre-timelocked-refund shape.
                NonInteractiveParametersLegacy.PreTimelockedRefund));

        Assert.That(contract.GetArkAddress().ToString(false), Is.EqualTo(QuotedLockupAddress));
    }

    // ─── A send quote whose two covenant destinations actually differ ───

    // Captured 2026-08-11 from the same stack, with the trader's refund address on a wallet of its
    // own. That is the whole point of this second vector: in the transcript above the trader and the
    // solver happened to share an address, so `nonInteractiveClaim` and `nonInteractiveRefund` were
    // pinned to the same script and the two could be swapped with no visible effect. A vector that
    // cannot tell two inputs apart cannot catch them being confused.
    private const string DistinctPaymentHash = "22fa82b7a24a4907c3955ca1044b4eef7a4e30aab0dfa96eae8e6435500fb5f4";
    private const string SolverClaimPkScript = "5120ec250c5be12707c56bee7a263fb1495e0fbdb733c9eb35a53b5e57e1e2ec2534";
    private const string TraderRefundPkScript = "5120535e2e7fb7a3fa9b3be74b13b813261497e3ad8a9d61cc45d233b4e0d21a7e73";
    private const uint DistinctRefundLocktime = 1786771492;

    private const string DistinctQuotedAddress =
        "tark1qr340xg400jtxat9hdd0ungyu6s05zjtdf85uj9smyzxshf98ndahaxvrkkd9cur3mwaagmq6sq49s3d8ed3gtdcjutx5cv0d3ysnytya2kpzq";

    [Test]
    public void OurDerivation_KeepsTheTwoCovenantDestinationsApart()
    {
        var delays = LightningCorridor.UnilateralDelays(TestServerInfo.WithSeconds(OperatorExitDelay));

        var contract = new VHTLCv2Contract(
            Descriptor(ArkdSigner),
            sender: Descriptor("02" + ClientRefundPubkey),
            receiver: Descriptor("02" + SolverPubkey),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(DistinctPaymentHash)), false),
            new LockTime(DistinctRefundLocktime),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            new NonInteractiveParameters(
                ECXOnlyPubKey.Create(Convert.FromHexString(EmulatorSigner)[1..]),
                // The solver's payout pins the claim leaf; the trader's own address pins the refund leaf.
                receiverPkScript: Convert.FromHexString(SolverClaimPkScript),
                senderPkScript: Convert.FromHexString(TraderRefundPkScript),
                NonInteractiveParametersLegacy.PreTimelockedRefund));

        Assert.That(contract.GetArkAddress().ToString(false), Is.EqualTo(DistinctQuotedAddress));
    }

    // ─── The receive corridor, same stack, captured after the solver began routing it ───

    private const string ReceivePaymentHash = "deb0e38ced1e41de6f92e70e80c418d2d356afaaa99e26f5939dbc7d3ef4772a";
    private const string PayoutPubkey = "7c2a5ee7f0d4f5f61b0b6b1d4c9a83a0e2f5c6d7889a0b1c2d3e4f5061728394";
    private const string PayoutPkScript = "5120ec250c5be12707c56bee7a263fb1495e0fbdb733c9eb35a53b5e57e1e2ec2534";
    private const string SolverRefundPkScript = "5120ec250c5be12707c56bee7a263fb1495e0fbdb733c9eb35a53b5e57e1e2ec2534";
    private const uint ReceiveRefundLocktime = 1786398939;

    private const string ReceiveQuotedLockupAddress =
        "tark1qr340xg400jtxat9hdd0ungyu6s05zjtdf85uj9smyzxshf98ndakg6x8qzl7uscskch3j76sc322z8rrlrqkja9slw7qxf03tq4s5mjr9f07u";

    [Test]
    public void OurReceiveDerivation_ReproducesTheAddressTheSolverQuoted()
    {
        // The same construction with the roles swapped, which is the only thing separating the two
        // corridors. Getting it backwards would still build and still produce a plausible address —
        // one the solver funds and we could never claim.
        var delays = LightningCorridor.UnilateralDelays(TestServerInfo.WithSeconds(OperatorExitDelay));

        var contract = new VHTLCv2Contract(
            Descriptor(ArkdSigner),
            sender: Descriptor("02" + SolverPubkey),
            receiver: Descriptor("02" + PayoutPubkey),
            new uint160(
                SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(ReceivePaymentHash)), false),
            new LockTime(ReceiveRefundLocktime),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            new NonInteractiveParameters(
                ECXOnlyPubKey.Create(Convert.FromHexString(EmulatorSigner)[1..]),
                receiverPkScript: Convert.FromHexString(PayoutPkScript),
                senderPkScript: Convert.FromHexString(SolverRefundPkScript),
                NonInteractiveParametersLegacy.PreTimelockedRefund));

        Assert.That(contract.GetArkAddress().ToString(false), Is.EqualTo(ReceiveQuotedLockupAddress));
    }

    private static OutputDescriptor Descriptor(string compressedHex) =>
        KeyExtensions.ParseOutputDescriptor(compressedHex, Network.RegTest);
}

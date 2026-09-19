using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Our covenant derivation against addresses the reference solver itself produced.
/// </summary>
/// <remarks>
/// Regenerate with <c>node generate-current-quotes.mjs &lt;solver repo&gt;</c>; the fixture records
/// which solver commit answered. The <c>send-stretched</c> vector is <c>send-distinct</c> with a
/// day-long refund horizon and nothing else changed, which is what makes it worth having: the two
/// share every other input, so a derivation that ignored the quoted solo-refund rung would produce
/// one address for both and fail here rather than at a live quote.
/// </remarks>
public class CurrentSolverDerivationTests
{
    [TestCase("send")]
    [TestCase("send-distinct")]
    [TestCase("send-stretched")]
    [TestCase("receive")]
    public void LocalDerivation_MatchesCurrentSolver(string name)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "ArkadeIntents", "Fixtures", "current_solver_quotes.json")));
        var root = fixture.RootElement;
        var vector = root.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
        string Field(string key) => vector.GetProperty(key).GetString()!;
        var delays = LightningCorridor.UnilateralDelays(TestServerInfo.WithSeconds(root.GetProperty("operatorExitDelay").GetUInt32()));

        // Read exactly as a client reads it off a quote: the published number when there is one,
        // the base ladder when there is not. `quotedAt` stands in for the client's own clock.
        var quotedAt = vector.TryGetProperty("quotedAt", out var at)
            ? at.GetInt64()
            : vector.GetProperty("refundLocktime").GetInt64();
        var soloRefund = LightningCorridor.ResolveSoloRefundDelay(
            vector.GetProperty("refundWithoutReceiverDelay").GetInt64(),
            delays,
            vector.GetProperty("refundLocktime").GetInt64(),
            quotedAt);
        var contract = new VHTLCv2Contract(
            KeyExtensions.ParseOutputDescriptor("02" + root.GetProperty("server").GetString(), Network.RegTest),
            KeyExtensions.ParseOutputDescriptor("02" + Field("sender"), Network.RegTest),
            KeyExtensions.ParseOutputDescriptor("02" + Field("receiver"), Network.RegTest),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(Field("paymentHash"))), false),
            new LockTime(vector.GetProperty("refundLocktime").GetUInt32()),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(soloRefund)),
            new VHTLCv2NonInteractiveClaim(Convert.FromHexString(Field("receiverScript")),
                ECXOnlyPubKey.Create(Convert.FromHexString(root.GetProperty("emulator").GetString()!))),
            new VHTLCv2NonInteractiveRefund(Convert.FromHexString(Field("senderScript")),
                ECXOnlyPubKey.Create(Convert.FromHexString(root.GetProperty("emulator").GetString()!)), true));

        Assert.That(contract.GetArkAddress().ToString(false), Is.EqualTo(Field("address")));
    }
}

using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

public class CurrentSolverDerivationTests
{
    [TestCase("send")]
    [TestCase("send-distinct")]
    [TestCase("receive")]
    public void LocalDerivation_MatchesCurrentSolver(string name)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "ArkadeIntents", "Fixtures", "current_solver_quotes.json")));
        var root = fixture.RootElement;
        var vector = root.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
        string Field(string key) => vector.GetProperty(key).GetString()!;
        var delays = LightningCorridor.UnilateralDelays(TestServerInfo.WithSeconds(root.GetProperty("operatorExitDelay").GetUInt32()));
        var contract = new VHTLCv2Contract(
            KeyExtensions.ParseOutputDescriptor("02" + root.GetProperty("server").GetString(), Network.RegTest),
            KeyExtensions.ParseOutputDescriptor("02" + Field("sender"), Network.RegTest),
            KeyExtensions.ParseOutputDescriptor("02" + Field("receiver"), Network.RegTest),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(Convert.FromHexString(Field("paymentHash"))), false),
            new LockTime(vector.GetProperty("refundLocktime").GetUInt32()),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            new VHTLCv2NonInteractiveClaim(Convert.FromHexString(Field("receiverScript")),
                ECXOnlyPubKey.Create(Convert.FromHexString(root.GetProperty("emulator").GetString()!))),
            new VHTLCv2NonInteractiveRefund(Convert.FromHexString(Field("senderScript")),
                ECXOnlyPubKey.Create(Convert.FromHexString(root.GetProperty("emulator").GetString()!)), true));

        Assert.That(contract.GetArkAddress().ToString(false), Is.EqualTo(Field("address")));
    }
}

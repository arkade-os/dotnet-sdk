using NArk.Abstractions.Intents;

namespace NArk.Tests.End2End.DelegatorServer;

public class DelegatorIntakeTests
{
    [Test]
    public async Task Delegate_accepts_cosigns_and_persists_intent()
    {
        await using var host = await InProcessDelegatorHost.StartAsync();
        await using var client = await DelegatingClientHarness.CreateAndDelegateAsync(host.BaseUrl);

        // The monitor sends a real delegation; the server validates, co-signs the forfeit, and persists
        // a WaitingToSubmit intent under the delegator wallet.
        await PollUntil(async () =>
            (await host.IntentStorage.GetIntents(states: [ArkIntentState.WaitingToSubmit])).Count > 0,
            TimeSpan.FromMinutes(2), "delegated intent was never persisted");

        var intent = (await host.IntentStorage.GetIntents(states: [ArkIntentState.WaitingToSubmit])).Single();
        Assert.That(intent.WalletId, Is.EqualTo(host.DelegatorWalletId));
        Assert.That(intent.PartialForfeits, Is.Not.Empty, "the co-signed forfeit must be stored");
        Assert.That(intent.IntentVtxos, Is.Not.Empty, "the intent must reference the delegated VTXO");

        TestContext.Progress.WriteLine(
            $"Persisted delegated intent {intent.IntentTxId} ({intent.PartialForfeits.Length} forfeit, " +
            $"{intent.IntentVtxos.Length} vtxo, validFrom={intent.ValidFrom})");
    }

    private static async Task PollUntil(Func<Task<bool>> condition, TimeSpan timeout, string failMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(1000);
        }
        Assert.Fail(failMessage);
    }
}

using NArk.Abstractions.VTXOs;

namespace NArk.Tests;

[TestFixture]
public class ArkVtxoTests
{
    private static ArkVtxo MakeVtxo(bool unrolled, Dictionary<string, string>? metadata) =>
        new(
            Script: "0014" + new string('0', 40),
            TransactionId: new string('a', 64),
            TransactionOutputIndex: 0,
            Amount: 50_000,
            SpentByTransactionId: null,
            SettledByTransactionId: null,
            Swept: false,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: null,
            ExpiresAtHeight: null,
            Unrolled: unrolled,
            Metadata: metadata);

    [Test]
    public void IsUnconfirmedOnchain_UnconfirmedBoarding_True()
    {
        // Boarding sync writes Metadata["Confirmed"] = bool.ToString() => "False".
        var vtxo = MakeVtxo(unrolled: true,
            new Dictionary<string, string> { [ArkVtxo.ConfirmedMetadataKey] = false.ToString() });
        Assert.That(vtxo.IsUnconfirmedOnchain(), Is.True);
    }

    [Test]
    public void IsUnconfirmedOnchain_ConfirmedBoarding_False()
    {
        var vtxo = MakeVtxo(unrolled: true,
            new Dictionary<string, string> { [ArkVtxo.ConfirmedMetadataKey] = true.ToString() });
        Assert.That(vtxo.IsUnconfirmedOnchain(), Is.False);
    }

    [Test]
    public void IsUnconfirmedOnchain_NoConfirmedMetadata_False()
    {
        // Regular off-chain VTXOs and arkd-reported unrolled VTXOs carry no
        // "Confirmed" key — they are not treated as unconfirmed-onchain.
        Assert.That(MakeVtxo(unrolled: false, null).IsUnconfirmedOnchain(), Is.False);
        Assert.That(MakeVtxo(unrolled: true, new Dictionary<string, string>()).IsUnconfirmedOnchain(), Is.False);
    }

    [Test]
    public void IsUnconfirmedOnchain_CaseInsensitiveTrue_False()
    {
        var vtxo = MakeVtxo(unrolled: true,
            new Dictionary<string, string> { [ArkVtxo.ConfirmedMetadataKey] = "true" });
        Assert.That(vtxo.IsUnconfirmedOnchain(), Is.False);
    }
}

using NArk.Arkade.Program.Models;
using NArk.ArkadeIntents.Programs;

namespace NArk.Tests.ArkadeIntents;

[TestFixture]
public class ArkadeIntentProgramsTests
{
    [Test]
    public void BtcToAsset_LoadsFromEmbeddedArtifact()
    {
        var program = ArkadeIntentPrograms.BtcToAsset;

        Assert.That(program.Name, Is.EqualTo("banco-btc-to-asset"));
        Assert.That(program.Version, Is.EqualTo(ArkadeProgram.SupportedVersion));
        Assert.That(program.Functions.Keys, Is.EquivalentTo(new[] { "fulfill", "cancel" }));

        // Typed params round-trip through the parser.
        Assert.That(program.Params!.Select(p => p.Name),
            Is.EqualTo(new[] { "makerWP", "wantAmount", "wantAssetTxid", "wantAssetGroupIndex", "server", "user" }));
        var wantAmount = program.Params!.Single(p => p.Name == "wantAmount");
        Assert.That(wantAmount.Type, Is.EqualTo(InputType.Int));

        // 'fulfill' is the covenant path; 'cancel' is pure tapscript.
        Assert.That(program.Functions["fulfill"].ScriptSegment, Is.Not.Null);
        Assert.That(program.Functions["cancel"].ScriptSegment, Is.Null);
    }

    [Test]
    public void AssetToBtc_LoadsFromEmbeddedArtifact()
    {
        var program = ArkadeIntentPrograms.AssetToBtc;

        Assert.That(program.Name, Is.EqualTo("banco-asset-to-btc"));
        Assert.That(program.Functions.Keys, Is.EquivalentTo(new[] { "fulfill", "cancel" }));
        Assert.That(program.Functions["fulfill"].ScriptSegment, Is.Not.Null);
    }

    [Test]
    public void Programs_AreCached_SameInstancePerAccess()
    {
        Assert.That(ArkadeIntentPrograms.BtcToAsset, Is.SameAs(ArkadeIntentPrograms.BtcToAsset));
        Assert.That(ArkadeIntentPrograms.AssetToBtc, Is.SameAs(ArkadeIntentPrograms.AssetToBtc));
    }
}

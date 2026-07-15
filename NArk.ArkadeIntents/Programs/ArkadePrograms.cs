using System.Reflection;
using System.Text.Json.Nodes;
using NArk.Arkade.Program;

namespace NArk.Arkade.Programs;

/// <summary>
/// The built-in Arkade program artifacts, parsed once from their embedded JSON and cached for the
/// process lifetime (first access pays a single parse; every later access is free). Each is an
/// <see cref="ArkadeProgram"/> template with unbound <c>$params</c> — bind the constructor args and
/// keys at contract construction (see <c>ArkProgramContract</c>).
/// </summary>
public static class ArkadePrograms
{
    private static readonly ArkadeArtifactParser Parser = new();

    private static readonly Lazy<ArkadeProgram> LazyBtcToAsset = Load("btc-to-asset.program.json");
    private static readonly Lazy<ArkadeProgram> LazyAssetToBtc = Load("asset-to-btc.program.json");

    /// <summary>
    /// Swap Ark BTC → an Arkade asset. The solver fills the covenant <c>fulfill</c> path (the
    /// emulator co-signs once the ArkadeScript covenant confirms the wanted asset lands on the
    /// maker's output); <c>user</c>+<c>server</c> can collaboratively <c>cancel</c>.
    /// </summary>
    public static ArkadeProgram BtcToAsset => LazyBtcToAsset.Value;

    /// <summary>Swap an Arkade asset → Ark BTC — the mirrored fill/cancel shape.</summary>
    public static ArkadeProgram AssetToBtc => LazyAssetToBtc.Value;

    private static Lazy<ArkadeProgram> Load(string fileName) => new(
        () =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = Array.Find(
                                   assembly.GetManifestResourceNames(),
                                   n => n.EndsWith(fileName, StringComparison.Ordinal))
                               ?? throw new InvalidOperationException(
                                   $"Embedded program artifact '{fileName}' was not found.");

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var json = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            return Parser.ParseArtifact(json);
        },
        LazyThreadSafetyMode.ExecutionAndPublication);
}

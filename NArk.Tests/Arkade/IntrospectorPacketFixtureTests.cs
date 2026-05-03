using System.Text.Json;
using NArk.Arkade.Introspector;

namespace NArk.Tests.Arkade;

/// <summary>
/// Drives <see cref="IntrospectorPacket"/> against the vendored
/// <c>introspector_packet.json</c> fixture from
/// <c>ArkLabsHQ/introspector pkg/arkade/testdata/</c>. Both <c>valid</c>
/// vectors must round-trip byte-for-byte; <c>invalid</c> vectors must reject.
/// </summary>
[TestFixture]
public class IntrospectorPacketFixtureTests
{
    private static FixtureRoot Fixture { get; } = LoadFixture();

    [TestCaseSource(nameof(ValidVectorNames))]
    public void Valid_Encode_ProducesFixtureBytes(string name)
    {
        var v = Fixture.Valid.Single(x => x.Name == name);
        var entries = v.Entries.Select(ToEntry).ToArray();
        var encoded = IntrospectorPacket.Serialize(entries);
        Assert.That(Convert.ToHexString(encoded).ToLowerInvariant(),
            Is.EqualTo(v.Encoded.ToLowerInvariant()),
            $"vector '{name}' did not encode to fixture bytes");
    }

    [TestCaseSource(nameof(ValidVectorNames))]
    public void Valid_Parse_ProducesFixtureEntries(string name)
    {
        var v = Fixture.Valid.Single(x => x.Name == name);
        var bytes = Convert.FromHexString(v.Encoded);
        var parsed = IntrospectorPacket.Parse(bytes);

        Assert.That(parsed, Has.Count.EqualTo(v.Entries.Count));
        for (var i = 0; i < parsed.Count; i++)
        {
            var actual = parsed[i];
            var expected = v.Entries[i];
            Assert.Multiple(() =>
            {
                Assert.That(actual.Vin, Is.EqualTo(expected.Vin), $"entry[{i}].vin");
                Assert.That(Convert.ToHexString(actual.Script).ToLowerInvariant(),
                    Is.EqualTo(expected.Script.ToLowerInvariant()), $"entry[{i}].script");
                // The fixture's witness is a list of pushes; the wire-level
                // witness blob is the EncodePushList() of that list.
                var expectedWitness = IntrospectorPacket.EncodePushList(
                    expected.Witness.Select(Convert.FromHexString).ToArray());
                Assert.That(actual.Witness, Is.EqualTo(expectedWitness),
                    $"entry[{i}].witness");
            });
        }
    }

    [TestCaseSource(nameof(InvalidValidateVectorNames))]
    public void Invalid_Validate_Rejected(string name)
    {
        // Fixtures with `entries` (no `encoded`) target the Validate rules:
        // empty packet, empty script, duplicate vin.
        var v = Fixture.Invalid.Single(x => x.Name == name);
        var entries = (v.Entries ?? Array.Empty<FixtureEntry>()).Select(ToEntry).ToArray();
        Assert.Throws<ArgumentException>(() => IntrospectorPacket.Validate(entries));
    }

    [TestCaseSource(nameof(InvalidParseVectorNames))]
    public void Invalid_Parse_Rejected(string name)
    {
        // Fixtures with `encoded` (no `entries`) target the wire-format parser:
        // truncated, trailing bytes, length fields exceeding the buffer.
        var v = Fixture.Invalid.Single(x => x.Name == name);
        var bytes = Convert.FromHexString(v.Encoded!);
        // Either parse rejects with FormatException or validation rejects after
        // a successful parse — both count as "the wire vector is illegal".
        Assert.That(() => IntrospectorPacket.Parse(bytes),
            Throws.TypeOf<FormatException>().Or.TypeOf<ArgumentException>());
    }

    [Test]
    public void EncodePushList_RoundTrip()
    {
        var pushes = new[]
        {
            Convert.FromHexString("01"),
            Convert.FromHexString("deadbeef"),
            [],
            Convert.FromHexString("ff"),
        };
        var bytes = IntrospectorPacket.EncodePushList(pushes);
        var decoded = IntrospectorPacket.DecodePushList(bytes);
        Assert.That(decoded, Has.Count.EqualTo(pushes.Length));
        for (var i = 0; i < pushes.Length; i++)
            Assert.That(decoded[i], Is.EqualTo(pushes[i]));
    }

    private static IntrospectorEntry ToEntry(FixtureEntry e)
    {
        // Fixture witness = list of pushes; wire witness = the encoded list.
        var pushes = (e.Witness ?? Array.Empty<string>()).Select(Convert.FromHexString).ToArray();
        var witnessBytes = IntrospectorPacket.EncodePushList(pushes);
        return new IntrospectorEntry((ushort)e.Vin, Convert.FromHexString(e.Script ?? ""), witnessBytes);
    }

    private static IEnumerable<string> ValidVectorNames()
        => Fixture.Valid.Select(v => v.Name);

    private static IEnumerable<string> InvalidValidateVectorNames()
        => Fixture.Invalid.Where(v => v.Encoded is null).Select(v => v.Name);

    private static IEnumerable<string> InvalidParseVectorNames()
        => Fixture.Invalid.Where(v => v.Encoded is not null).Select(v => v.Name);

    private static FixtureRoot LoadFixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Arkade", "Fixtures", "introspector_packet.json");
        var json = File.ReadAllText(path);
        var fixture = JsonSerializer.Deserialize<FixtureRoot>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return fixture ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record FixtureRoot(IReadOnlyList<ValidVector> Valid, IReadOnlyList<InvalidVector> Invalid);
    public sealed record ValidVector(string Name, string Encoded, IReadOnlyList<FixtureEntry> Entries);
    public sealed record InvalidVector(string Name, string? Encoded, IReadOnlyList<FixtureEntry>? Entries);
    public sealed record FixtureEntry(int Vin, string? Script, IReadOnlyList<string>? Witness);
}

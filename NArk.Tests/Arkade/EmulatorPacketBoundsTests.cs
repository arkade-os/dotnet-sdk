using NArk.Arkade.Emulator;

namespace NArk.Tests.Arkade;

/// <summary>
/// The emulator's decoder (<c>pkg/arkade/emulator_packet.go</c>) caps entry
/// count (≤1000), per-entry script length (1–10000), and the encoded witness
/// blob length (≤1,000,000). These bounds are part of the wire contract — an
/// over-limit packet must be rejected explicitly, not merely caught later by a
/// buffer underrun.
/// </summary>
[TestFixture]
public class EmulatorPacketBoundsTests
{
    private static EmulatorEntry ScriptOfLength(ushort vin, int len) =>
        new(vin, Enumerable.Repeat((byte)0x51, len).ToArray(), Array.Empty<byte[]>());

    [Test]
    public void EntryCount_AtMax1000_Ok_Over1001_Rejected()
    {
        var atMax = Enumerable.Range(0, 1000).Select(i => ScriptOfLength((ushort)i, 1)).ToArray();
        Assert.That(() => new EmulatorPacket(atMax), Throws.Nothing);

        var overMax = Enumerable.Range(0, 1001).Select(i => ScriptOfLength((ushort)i, 1)).ToArray();
        Assert.That(() => new EmulatorPacket(overMax), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void ScriptLength_AtMax10000_Ok_Over10001_Rejected()
    {
        Assert.That(() => new EmulatorPacket([ScriptOfLength(0, 10_000)]), Throws.Nothing);
        Assert.That(() => new EmulatorPacket([ScriptOfLength(0, 10_001)]), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void WitnessBlobLength_OverMax_Rejected()
    {
        // A single ~1 MB push encodes to a witness blob just over 1,000,000 bytes.
        var entry = new EmulatorEntry(0, [0x51], new[] { new byte[1_000_001] });
        Assert.That(() => new EmulatorPacket([entry]), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Parse_DeclaredEntryCountOverMax_Rejected()
    {
        // Wire blob declaring 1001 entries (compactSize fd e9 03) must be rejected
        // at the count read, before attempting to read 1001 entries.
        var bytes = Convert.FromHexString("fde903");
        Assert.That(() => EmulatorPacket.FromBytes(bytes),
            Throws.TypeOf<FormatException>().Or.TypeOf<ArgumentException>());
    }

    [Test]
    public void Bounds_AreExposedAsConstants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EmulatorPacket.MaxEntryCount, Is.EqualTo(1000));
            Assert.That(EmulatorPacket.MaxScriptLength, Is.EqualTo(10_000));
            Assert.That(EmulatorPacket.MaxWitnessLength, Is.EqualTo(1_000_000));
        });
    }
}

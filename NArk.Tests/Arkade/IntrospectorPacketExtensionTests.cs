using NArk.Arkade.Introspector;
using NArk.Core.Assets;
using NBitcoin;

namespace NArk.Tests.Arkade;

/// <summary>
/// Verifies <see cref="IntrospectorPacket"/> works as an
/// <see cref="IExtensionPacket"/> — i.e. it rides in the same OP_RETURN
/// envelope the asset packet uses, and consumers can recover it from a
/// parsed transaction via <see cref="IntrospectorPacket.FromExtension"/> /
/// <see cref="IntrospectorPacket.FromTransaction"/>.
/// </summary>
[TestFixture]
public class IntrospectorPacketExtensionTests
{
    private static IntrospectorPacket SamplePacket() =>
        new([
            new IntrospectorEntry(0, [0x51, 0xc4], [Convert.FromHexString("deadbeef")]),
            new IntrospectorEntry(2, [0x52], [[0xff], [0x00]]),
        ]);

    [Test]
    public void FromExtension_RoundTrip_AlonePacket()
    {
        var original = SamplePacket();
        var ext = new Extension([original]);

        var serialized = ext.Serialize();
        var parsedExt = Extension.FromScript(new Script(serialized));
        var recovered = IntrospectorPacket.FromExtension(parsedExt);

        Assert.That(recovered, Is.Not.Null);
        Assert.That(recovered!.Entries, Has.Count.EqualTo(original.Entries.Count));
        for (var i = 0; i < original.Entries.Count; i++)
        {
            Assert.That(recovered.Entries[i].Vin, Is.EqualTo(original.Entries[i].Vin));
            Assert.That(recovered.Entries[i].Script, Is.EqualTo(original.Entries[i].Script));
            Assert.That(recovered.Entries[i].Witness.Count,
                Is.EqualTo(original.Entries[i].Witness.Count));
            for (var j = 0; j < original.Entries[i].Witness.Count; j++)
                Assert.That(recovered.Entries[i].Witness[j],
                    Is.EqualTo(original.Entries[i].Witness[j]));
        }
    }

    [Test]
    public void FromTransaction_FindsPacketAcrossOutputs()
    {
        var packet = SamplePacket();
        var ext = new Extension([packet]);

        // Build a 2-output tx: a normal P2WPKH-shaped placeholder + the OP_RETURN extension.
        var tx = Transaction.Create(Network.Main);
        tx.Outputs.Add(new TxOut(Money.Coins(1), new Script(OpcodeType.OP_TRUE)));
        tx.Outputs.Add(ext.ToTxOut());

        var recovered = IntrospectorPacket.FromTransaction(tx);
        Assert.That(recovered, Is.Not.Null);
        Assert.That(recovered!.Entries.Count, Is.EqualTo(packet.Entries.Count));
    }

    [Test]
    public void FromTransaction_NoExtension_ReturnsNull()
    {
        var tx = Transaction.Create(Network.Main);
        tx.Outputs.Add(new TxOut(Money.Coins(1), new Script(OpcodeType.OP_TRUE)));
        Assert.That(IntrospectorPacket.FromTransaction(tx), Is.Null);
    }

    [Test]
    public void PacketTypeId_Is_0x01()
    {
        // Locked at 0x01 by the introspector reference. Don't drift.
        Assert.That(IntrospectorPacket.PacketTypeId, Is.EqualTo((byte)0x01));
        Assert.That(((IExtensionPacket)SamplePacket()).PacketType, Is.EqualTo((byte)0x01));
    }
}

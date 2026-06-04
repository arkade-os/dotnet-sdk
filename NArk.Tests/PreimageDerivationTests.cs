using System.Text;
using NArk.Abstractions.Extensions;
using NArk.Swaps.Services;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Tests;

/// <summary>
/// Pins the cross-SDK deterministic-preimage message format produced by
/// <see cref="SwapsManagementService.BuildPreimageMessage"/>:
/// <c>PreimageTag || x-only pubkey (32B) || u32 LE index</c>.
///
/// Anchoring on the canonical x-only public key — not the descriptor's
/// non-canonical <c>.ToString()</c> — is what lets a restored wallet, which
/// rediscovers the swap via a <em>reconstructed</em> bare receiver descriptor,
/// re-derive the same preimage the original (HD signing) descriptor produced.
/// </summary>
[TestFixture]
public class PreimageDerivationTests
{
    private const string Tag = "Arkade-Boltz-Preimage-v1";

    [Test]
    public void Message_IsTagPlusXOnlyPubkeyPlusIndexLittleEndian()
    {
        var key = new Key();
        var descriptor = OutputDescriptor.Parse($"tr({key.PubKey.ToHex()})", Network.Main);
        var xOnly = descriptor.Extract().XOnlyPubKey.ToBytes(); // 32 bytes

        var message = SwapsManagementService.BuildPreimageMessage(descriptor, index: 0);

        var expected = Encoding.UTF8.GetBytes(Tag)
            .Concat(xOnly)
            .Concat(new byte[] { 0x00, 0x00, 0x00, 0x00 }) // u32 LE, index 0
            .ToArray();
        Assert.That(message, Is.EqualTo(expected));
    }

    [Test]
    public void Message_IndexEncodedAsUInt32LittleEndian()
    {
        var descriptor = OutputDescriptor.Parse($"tr({new Key().PubKey.ToHex()})", Network.Main);

        var message = SwapsManagementService.BuildPreimageMessage(descriptor, index: 1);

        Assert.That(message[^4..], Is.EqualTo(new byte[] { 0x01, 0x00, 0x00, 0x00 }));
    }

    [Test]
    public void Message_IsIndependentOfDescriptorStringForm()
    {
        // The real restore scenario: an HD signing descriptor (key origin + wildcard)
        // at create time vs the bare receiver descriptor a restore reconstructs — same
        // key, very different .ToString(). The derived message must match regardless.
        var accountXpub = new ExtKey().Neuter().ToString(Network.Main);
        var hdDescriptor = OutputDescriptor.Parse(
            $"tr([d34db33f/86'/0'/0']{accountXpub}/0/*)", Network.Main);
        var derivedXOnly = hdDescriptor.Extract().XOnlyPubKey;
        var bareDescriptor = OutputDescriptor.Parse(
            $"tr({Convert.ToHexString(derivedXOnly.ToBytes()).ToLowerInvariant()})", Network.Main);

        Assert.That(hdDescriptor.ToString(), Is.Not.EqualTo(bareDescriptor.ToString()),
            "precondition: HD signing vs bare receiver descriptors serialise differently");

        Assert.That(
            SwapsManagementService.BuildPreimageMessage(hdDescriptor, 0),
            Is.EqualTo(SwapsManagementService.BuildPreimageMessage(bareDescriptor, 0)));
    }
}

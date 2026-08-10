using System.Text.Json;
using NArk.ArkadeIntents.Nostr;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents.Nostr;

/// <summary>
/// Pins NIP-01 event identity and signing against a nostr-tools-produced event.
/// </summary>
/// <remarks>
/// The id is a hash of a hand-rolled serialization, so the escaping rules are load-bearing: a
/// general-purpose JSON writer escapes more than NIP-01 says to, which changes the hash, which
/// changes the id, which breaks every signature. And the signature is what makes a solver's quote
/// non-repudiable, so verifying it is not optional politeness — on a shared relay anyone can publish
/// anything under any claimed author.
/// </remarks>
[TestFixture]
public class NostrEventTests
{
    private static readonly Vectors Fixture = LoadFixture();

    [Test]
    public void ComputeId_ReproducesTheReferenceHash()
    {
        var ev = Fixture.Event;

        Assert.That(
            NostrEventFactory.ComputeId(ev.Pubkey, ev.CreatedAt, ev.Kind, ev.Tags, ev.Content),
            Is.EqualTo(ev.Id));
    }

    [Test]
    public void Verify_AcceptsAReferenceSignedEvent()
    {
        Assert.That(NostrEventFactory.Verify(Fixture.Event), Is.True);
    }

    [Test]
    public void Verify_RejectsContentSwappedUnderARealSignature()
    {
        // The id covers the content, so a genuine signature cannot be lifted onto a different
        // payload — which is the whole reason the id is checked as well as the signature.
        var tampered = new NostrEvent
        {
            Id = Fixture.Event.Id,
            Pubkey = Fixture.Event.Pubkey,
            CreatedAt = Fixture.Event.CreatedAt,
            Kind = Fixture.Event.Kind,
            Tags = Fixture.Event.Tags,
            Content = "something else entirely",
            Sig = Fixture.Event.Sig,
        };

        Assert.That(NostrEventFactory.Verify(tampered), Is.False);
    }

    [Test]
    public void Verify_RejectsAnEventSignedByAnotherKey()
    {
        var impostor = new Key();
        var signed = NostrEventFactory.Sign(impostor, 4859, "hello");
        var claimingSomeoneElse = new NostrEvent
        {
            Id = signed.Id,
            Pubkey = Fixture.Event.Pubkey,
            CreatedAt = signed.CreatedAt,
            Kind = signed.Kind,
            Tags = signed.Tags,
            Content = signed.Content,
            Sig = signed.Sig,
        };

        Assert.That(NostrEventFactory.Verify(claimingSomeoneElse), Is.False);
    }

    [Test]
    public void Sign_ProducesSomethingWeCanVerify()
    {
        var signed = NostrEventFactory.Sign(
            new Key(), 4859, "sealed", [["p", new string('a', 64)]], createdAt: 1786000000);

        Assert.Multiple(() =>
        {
            Assert.That(NostrEventFactory.Verify(signed), Is.True);
            Assert.That(signed.FirstTag("p"), Is.EqualTo(new string('a', 64)));
            Assert.That(signed.FirstTag("t"), Is.Null);
        });
    }

    [Test]
    public void ComputeId_EscapesExactlyWhatNip01Says()
    {
        // Only these seven get escaped. A writer that also escaped, say, a non-ASCII character or
        // an angle bracket would hash differently while looking perfectly valid.
        var id = NostrEventFactory.ComputeId(
            new string('b', 64), 1, 1, [], "a\"b\\c\nd\re\tf<g>h&iéj");

        Assert.That(id, Has.Length.EqualTo(64));
        Assert.That(id, Does.Match("^[0-9a-f]{64}$"));
    }

    private static Vectors LoadFixture()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory, "ArkadeIntents", "Fixtures", "nip44.json");
        return JsonSerializer.Deserialize<Vectors>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Failed to load fixture {path}");
    }

    public sealed record Vectors(NostrEvent Event);
}

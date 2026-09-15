using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests;

/// <summary>
/// Pins the upstream contract the SDK relies on since the local <c>ECXOnlyPubKeyComparer</c> was
/// dropped: <c>NBitcoin.Secp256k1</c> 4.0.1 gives <see cref="ECXOnlyPubKey"/> value-based
/// <c>Equals</c>/<c>==</c> on top of the byte-based <c>GetHashCode</c> it already had.
///
/// Nothing enforces that floor transitively — <c>NBitcoin</c> does not depend on
/// <c>NBitcoin.Secp256k1</c> at all, so the explicit package pin is the only thing holding it. On
/// 4.0.0 every assertion below about two distinct instances silently reverts to reference equality,
/// which would break the un-compared <c>HashSet</c>/<c>Dictionary</c> keyed on server signers.
/// </summary>
[TestFixture]
public class ECXOnlyPubKeyValueEqualityTests
{
    [Test]
    public void Distinct_instances_of_the_same_key_are_equal()
    {
        var (a, b) = SameKeyTwice();

        Assert.Multiple(() =>
        {
            Assert.That(ReferenceEquals(a, b), Is.False, "the two instances must not be the same object");
            Assert.That(a == b, Is.True);
            Assert.That(a != b, Is.False);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        });
    }

    [Test]
    public void Different_keys_are_not_equal()
    {
        var a = NewKey();
        var b = NewKey();

        Assert.Multiple(() =>
        {
            Assert.That(a == b, Is.False);
            Assert.That(a.Equals(b), Is.False);
        });
    }

    [Test]
    public void Null_comparisons_do_not_throw()
    {
        var key = NewKey();
        ECXOnlyPubKey? nothing = null;
        ECXOnlyPubKey? alsoNothing = null;

        Assert.Multiple(() =>
        {
            Assert.That(nothing == alsoNothing, Is.True);
            Assert.That(key == nothing, Is.False);
            Assert.That(nothing == key, Is.False);
            Assert.That(key != nothing, Is.True);
        });
    }

    [Test]
    public void HashSet_without_a_comparer_collapses_the_same_key()
    {
        var (a, b) = SameKeyTwice();

        var set = new HashSet<ECXOnlyPubKey> { a };

        Assert.Multiple(() =>
        {
            Assert.That(set.Contains(b), Is.True);
            Assert.That(set.Add(b), Is.False);
            Assert.That(set, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Dictionary_without_a_comparer_looks_up_the_same_key()
    {
        var (a, b) = SameKeyTwice();

        // Shaped like the DeprecatedSigners map the transports build from the wire response and
        // DestinationSafety/ServerKeyRotationSweepPolicy then probe with keys parsed elsewhere.
        var deprecatedSigners = new Dictionary<ECXOnlyPubKey, long> { [a] = 1_700_000_000L };

        Assert.Multiple(() =>
        {
            Assert.That(deprecatedSigners.ContainsKey(b), Is.True);
            Assert.That(deprecatedSigners.TryGetValue(b, out var cutoff), Is.True);
            Assert.That(cutoff, Is.EqualTo(1_700_000_000L));
        });
    }

    [Test]
    public void ToDictionary_does_not_throw_on_the_same_key_reaching_it_twice()
    {
        var (a, b) = SameKeyTwice();

        Assert.That(
            () => new[] { a, b }.ToDictionary(k => k, _ => 0L),
            Throws.ArgumentException,
            "duplicate signers on the wire must collide rather than produce two entries");
    }

    [Test]
    public void NofNMultisigTapScript_Parse_dedupes_a_repeated_owner()
    {
        var key = NewKey();
        var script = new Script(
            Op.GetPushOp(key.ToBytes()), OpcodeType.OP_CHECKSIGVERIFY,
            Op.GetPushOp(key.ToBytes()), OpcodeType.OP_CHECKSIG);

        var parsed = NofNMultisigTapScript.Parse(new ScriptReader(script.ToBytes()));

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Owners, Has.Length.EqualTo(1));
            Assert.That(parsed.Owners[0], Is.EqualTo(key));
        });
    }

    /// <summary>Two independently parsed instances carrying identical bytes.</summary>
    private static (ECXOnlyPubKey First, ECXOnlyPubKey Second) SameKeyTwice()
    {
        var bytes = NewKey().ToBytes();
        return (ECXOnlyPubKey.Create(bytes), ECXOnlyPubKey.Create(bytes));
    }

    private static ECXOnlyPubKey NewKey()
        => ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
}

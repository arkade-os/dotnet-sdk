using NArk.Arkade.Crypto;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Smoke tests for <see cref="ArkadeScriptHash"/>. The exact tagged-hash bytes
/// are dependent on BIP-340's <c>SHA256(SHA256(tag) || SHA256(tag) || msg)</c>
/// computation; we treat NBitcoin's <see cref="SHA256.InitializeTagged"/> as
/// the source of truth and just verify deterministic + independent + tweak-
/// validity properties here. Cross-SDK byte-equal vectors will land alongside
/// the upcoming ArkadeVtxoScript tests once we have ts-sdk-side fixtures.
/// </summary>
[TestFixture]
public class ArkadeScriptHashTests
{
    [Test]
    public void Compute_IsDeterministic()
    {
        ReadOnlySpan<byte> script = stackalloc byte[] { 0x51, 0xc4, 0xc6 };
        var a = ArkadeScriptHash.Compute(script);
        var b = ArkadeScriptHash.Compute(script);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Has.Length.EqualTo(32));
    }

    [Test]
    public void Compute_DifferentScripts_DifferentDigests()
    {
        var a = ArkadeScriptHash.Compute([0x51]);
        var b = ArkadeScriptHash.Compute([0x52]);
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void Tweak_ProducesValidTweakedKey()
    {
        // Generate a random introspector pubkey, tweak with a fixed script,
        // and verify the result is a 32-byte x-only pubkey.
        var seed = new byte[32];
        new Random(42).NextBytes(seed);
        var keyMaterial = new Key(seed);
        var introspectorPubKey = keyMaterial.PubKey.GetTaprootFullPubKey().OutputKey;

        var tweaked = ArkadeScriptHash.Tweak(introspectorPubKey, [0x51, 0xc4, 0xc6]);
        Assert.That(tweaked.ToBytes(), Has.Length.EqualTo(32));

        // Same tweak applied twice yields the same key.
        var tweaked2 = ArkadeScriptHash.Tweak(introspectorPubKey, [0x51, 0xc4, 0xc6]);
        Assert.That(tweaked.ToBytes(), Is.EqualTo(tweaked2.ToBytes()));

        // Different scripts → different tweaked keys.
        var tweakedOther = ArkadeScriptHash.Tweak(introspectorPubKey, [0x52, 0xc4, 0xc6]);
        Assert.That(tweaked.ToBytes(), Is.Not.EqualTo(tweakedOther.ToBytes()));
    }
}

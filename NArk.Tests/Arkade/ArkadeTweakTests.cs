using System.Text;
using NArk.Arkade.Crypto;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Smoke tests for <see cref="ArkadeTweak"/>. The exact tagged-hash bytes
/// are dependent on BIP-340's <c>SHA256(SHA256(tag) || SHA256(tag) || msg)</c>
/// computation; we treat NBitcoin's <see cref="SHA256.InitializeTagged"/> as
/// the source of truth and just verify deterministic + independent + tweak-
/// validity properties here.
/// </summary>
[TestFixture]
public class ArkadeTweakTests
{
    [Test]
    public void Compute_IsDeterministic()
    {
        ReadOnlySpan<byte> script = stackalloc byte[] { 0x51, 0xc4, 0xc6 };
        var a = ArkadeTweak.ComputeScriptHash(script);
        var b = ArkadeTweak.ComputeScriptHash(script);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Has.Length.EqualTo(32));
    }

    [Test]
    public void Compute_DifferentScripts_DifferentDigests()
    {
        var a = ArkadeTweak.ComputeScriptHash([0x51]);
        var b = ArkadeTweak.ComputeScriptHash([0x52]);
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void Tweak_ProducesValidTweakedKey()
    {
        // Generate a random emulator pubkey, tweak with a fixed script,
        // and verify the result is a 32-byte x-only pubkey.
        var seed = new byte[32];
        new Random(42).NextBytes(seed);
        var keyMaterial = new Key(seed);
        var emulatorPubKey = keyMaterial.PubKey.GetTaprootFullPubKey().OutputKey;

        var tweaked = ArkadeTweak.Tweak(emulatorPubKey, [0x51, 0xc4, 0xc6]);
        Assert.That(tweaked.ToBytes(), Has.Length.EqualTo(32));

        // Same tweak applied twice yields the same key.
        var tweaked2 = ArkadeTweak.Tweak(emulatorPubKey, [0x51, 0xc4, 0xc6]);
        Assert.That(tweaked.ToBytes(), Is.EqualTo(tweaked2.ToBytes()));

        // Different scripts → different tweaked keys.
        var tweakedOther = ArkadeTweak.Tweak(emulatorPubKey, [0x52, 0xc4, 0xc6]);
        Assert.That(tweaked.ToBytes(), Is.Not.EqualTo(tweakedOther.ToBytes()));
    }

    [Test]
    public void Tweak_FromCompressedEmulatorKey_MatchesXOnlyTweak()
    {
        // GET /v1/info returns a 33-byte *compressed* signerPubkey; tweaking it
        // via the ECPubKey overload must equal tweaking its x-only form — parity
        // is dropped, matching the ts-sdk / emulator reference.
        var emulatorPubKey = ECPubKey.Create(new Key().PubKey.ToBytes());
        var script = new byte[] { 0x51, 0xc4 };

        var fromCompressed = ArkadeTweak.Tweak(emulatorPubKey, script);
        var fromXOnly = ArkadeTweak.Tweak(new TaprootPubKey(emulatorPubKey.ToXOnlyPubKey().ToBytes()), script);

        Assert.That(fromCompressed.ToBytes(), Is.EqualTo(fromXOnly.ToBytes()));
    }

    // secp256k1 group order N minus 1. Multiplying a scalar by this tweak negates it —
    // used below to mirror btcec's ModNScalar.Negate() step from the reference test,
    // since NBitcoin.Secp256k1's ECPrivKey exposes no direct negation.
    private static readonly byte[] SecpOrderMinusOne =
        Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364140");

    private static byte[] PrivKeyBytesFromSeedWithParity(string seed, bool wantOddCompressedPrefix)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(seed));
        var priv = ECPrivKey.Create(digest);
        var isOdd = priv.CreatePubKey().ToBytes(true)[0] == 0x03;
        if (isOdd == wantOddCompressedPrefix) return digest;

        var negated = priv.TweakMul(SecpOrderMinusOne);
        Span<byte> negatedBytes = stackalloc byte[32];
        negated.WriteToSpan(negatedBytes);
        return negatedBytes.ToArray();
    }

    // test vectors from https://github.com/arkade-os/emulator/blob/master/pkg/arkade/tweak_test.go#L23-L50, commit 210d327
    private static IEnumerable<TestCaseData> ScriptKeyTweakingVectors()
    {
        yield return new TestCaseData(
                PrivKeyBytesFromSeedWithParity("private-key-seed-even", wantOddCompressedPrefix: false), (byte?)null)
            .SetName("{m}_even");
        yield return new TestCaseData(
                PrivKeyBytesFromSeedWithParity("private-key-seed-odd", wantOddCompressedPrefix: true), (byte?)null)
            .SetName("{m}_odd");
        yield return new TestCaseData(
                Convert.FromHexString("05717677ccec3c6ec975b8356b104808b6e149b82d9816d2d7c3b25dd658c220"), (byte?)0x03)
            .SetName("{m}_even_to_tweaked_odd");
        yield return new TestCaseData(
                Convert.FromHexString("2b6f9e9c6b1b6ada475009bb6ac01e7cacc5879ab2610b1ad017cf7e467665af"), (byte?)0x02)
            .SetName("{m}_even_to_tweaked_even");
    }

    /// <summary>
    /// Mirrors ComputeArkadeScriptPrivateKey from the Go emulator: negate the private
    /// key's scalar if its compressed pubkey is odd, then add the tagged script hash
    /// as a scalar. This is the emulator-side counterpart of <see cref="ArkadeTweak.Tweak(TaprootPubKey,ReadOnlySpan{byte})"/>,
    /// which only ever sees the public key.
    /// </summary>
    private static ECPrivKey ComputeArkadeScriptPrivateKey(ECPrivKey priv, byte[] scriptHash)
    {
        var isOdd = priv.CreatePubKey().ToBytes(true)[0] == 0x03;
        var normalized = isOdd ? priv.TweakMul(SecpOrderMinusOne) : priv;
        return normalized.TweakAdd(scriptHash);
    }

    [TestCaseSource(nameof(ScriptKeyTweakingVectors))]
    public void Tweak_MatchesEmulatorReferencePrivateKeyTweaking(byte[] privKeyBytes, byte? expectedTweakedPrefix)
    {
        var script = "OP_TRUE"u8.ToArray();
        var scriptHash = ArkadeTweak.ComputeScriptHash(script);

        var priv = ECPrivKey.Create(privKeyBytes);
        var originalXOnly = priv.CreatePubKey().ToXOnlyPubKey().ToBytes();

        var tweakedPriv = ComputeArkadeScriptPrivateKey(priv, scriptHash);
        var expectedTweakedPub = ArkadeTweak.Tweak(new TaprootPubKey(originalXOnly), script);

        // The public-key-only tweak this SDK exposes must be bit-compatible with what
        // the emulator derives by tweaking its own private key.
        Assert.That(tweakedPriv.CreatePubKey().ToXOnlyPubKey().ToBytes(), Is.EqualTo(expectedTweakedPub.ToBytes()));

        var message = System.Security.Cryptography.SHA256.HashData("yo"u8.ToArray());
        var signature = tweakedPriv.SignBIP340(message);

        var tweakedXOnlyPub = ECXOnlyPubKey.Create(expectedTweakedPub.ToBytes());
        Assert.That(tweakedXOnlyPub.SigVerifyBIP340(signature, message), Is.True);

        var originalXOnlyPub = ECXOnlyPubKey.Create(originalXOnly);
        Assert.That(originalXOnlyPub.SigVerifyBIP340(signature, message), Is.False);

        if (expectedTweakedPrefix is { } prefix)
            Assert.That(tweakedPriv.CreatePubKey().ToBytes(true)[0], Is.EqualTo(prefix));
    }
}

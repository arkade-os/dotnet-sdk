using NArk.Abstractions.Extensions;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Tests;

/// <summary>
/// Tests for the MuSig2 parity fix in NSecWalletSigner.
/// Verifies that odd-parity (03 prefix) nsec wallets can participate
/// in batch signing after the tr() descriptor parity loss bug.
/// </summary>
[TestFixture]
public class NSecWalletSignerParityTests
{
    // Synthetic test keys — two odd-parity (03 prefix) and one even-parity (02 prefix)
    private static readonly (string PrivKeyHex, string CompressedHex, string XOnlyHex)[] OddParityKeys =
    [
        (
            "f088d89eb60840d3ca4880ebfb827bf18e6af21b361cc13f69df230ed8246b3c",
            "035ab45f03588747ef06b8aa0ddd849cbc057382960379d2cce2b211678c3d98d9",
            "5ab45f03588747ef06b8aa0ddd849cbc057382960379d2cce2b211678c3d98d9"
        ),
        (
            "93817a2e829d4b75f2ab8d531f49284f24289466e5a845d4c79835d6e432fba6",
            "03702ed49586ad523cfc9df6d25f44344caaba9e348d57e9bf98c3ef7f1bb620a3",
            "702ed49586ad523cfc9df6d25f44344caaba9e348d57e9bf98c3ef7f1bb620a3"
        ),
    ];

    private static readonly (string PrivKeyHex, string CompressedHex, string XOnlyHex) EvenParityKey =
    (
        "dd44577a074c6ff01f7c298b8e6d2020b6d4e9af8f0456eb3ba667ea687491c0",
        "02cdcd6f44e6456d40b5102a89f5166badace021a0b6f10cc99d9d69cf9c63e1fd",
        "cdcd6f44e6456d40b5102a89f5166badace021a0b6f10cc99d9d69cf9c63e1fd"
    );

    private static NSecWalletSigner CreateSigner(string privKeyHex)
    {
        var privKey = ECPrivKey.Create(Convert.FromHexString(privKeyHex));
        return new NSecWalletSigner(privKey);
    }

    /// <summary>
    /// Build a tr() descriptor from x-only hex, simulating what production stores
    /// in SignerDescriptor (which loses the parity prefix).
    /// </summary>
    private static OutputDescriptor MakeSignerDescriptor(string xOnlyHex)
    {
        return OutputDescriptor.Parse($"tr({xOnlyHex})", Network.Main);
    }

    /// <summary>
    /// Build a tr() descriptor from compressed hex, simulating what production stores
    /// in AccountDescriptor (which preserves the parity prefix).
    /// </summary>
    private static OutputDescriptor MakeAccountDescriptor(string compressedHex)
    {
        return OutputDescriptor.Parse($"tr({compressedHex})", Network.Main);
    }

    [Test]
    public void OddParityKeys_HaveOddParityPrefix()
    {
        foreach (var key in OddParityKeys)
        {
            var signer = CreateSigner(key.PrivKeyHex);
            var descriptor = MakeSignerDescriptor(key.XOnlyHex);
            var pubKey = signer.GetPubKey(descriptor).Result;
            var compressed = Convert.ToHexString(pubKey.ToBytes()).ToLowerInvariant();

            Assert.That(compressed, Does.StartWith("03"),
                $"Key should have odd parity (03 prefix), got: {compressed}");
        }
    }

    [Test]
    public void EvenParityKey_HasEvenParityPrefix()
    {
        var signer = CreateSigner(EvenParityKey.PrivKeyHex);
        var descriptor = MakeSignerDescriptor(EvenParityKey.XOnlyHex);
        var pubKey = signer.GetPubKey(descriptor).Result;
        var compressed = Convert.ToHexString(pubKey.ToBytes()).ToLowerInvariant();

        Assert.That(compressed, Does.StartWith("02"),
            $"Key should have even parity (02 prefix), got: {compressed}");
    }

    [Test]
    public void DescriptorToPubKey_AlwaysReturnsEvenParity()
    {
        // This demonstrates the NBitcoin tr() serialization bug:
        // tr() strips the prefix byte → on parse, assumes even parity (02)
        foreach (var key in OddParityKeys)
        {
            var descriptor = MakeSignerDescriptor(key.XOnlyHex);
            var descriptorPubKey = descriptor.ToPubKey();
            var compressed = Convert.ToHexString(descriptorPubKey.ToBytes()).ToLowerInvariant();

            Assert.That(compressed, Does.StartWith("02"),
                $"descriptor.ToPubKey() should always return 02 prefix for tr() descriptors, got: {compressed}");
        }
    }

    [Test]
    public void GetPubKey_ReturnsCorrectParityNotDescriptorParity()
    {
        // GetPubKey must return the actual key (03...) not what the descriptor gives (02...)
        foreach (var key in OddParityKeys)
        {
            var signer = CreateSigner(key.PrivKeyHex);
            var descriptor = MakeSignerDescriptor(key.XOnlyHex);

            var signerPubKey = signer.GetPubKey(descriptor).Result;
            var descriptorPubKey = descriptor.ToPubKey();

            // They should NOT be equal — signer returns 03, descriptor gives 02
            Assert.That(signerPubKey, Is.Not.EqualTo(descriptorPubKey),
                "GetPubKey should return actual key with correct parity, not descriptor's wrong parity");

            // But x-only keys should match
            Assert.That(
                signerPubKey.ToXOnlyPubKey().ToBytes(),
                Is.EqualTo(descriptorPubKey.ToXOnlyPubKey().ToBytes()),
                "X-only keys should match despite parity difference");
        }
    }

    [TestCaseSource(nameof(AllKeys))]
    public void GenerateNonces_WorksWithCorrectParityMusigContext(
        string privKeyHex, string xOnlyHex)
    {
        // Simulate the fixed production flow:
        // 1. signer.GetPubKey() returns correct-parity key
        // 2. MusigContext is built with that correct-parity key
        // 3. GenerateNonces uses the original private key (no negation)
        var signer = CreateSigner(privKeyHex);
        var descriptor = MakeSignerDescriptor(xOnlyHex);

        // The signer's actual pubkey (what IntentGenerationService sends to the server)
        var signerPubKey = signer.GetPubKey(descriptor).Result;

        // Create a fake server key for the MuSig context
        var serverKey = ECPubKey.Create(Convert.FromHexString(
            "02381e98d39e102619de06b87a93c50e9a1cc95988bccf474cc7f26fa5df62283a"));

        var cosignerKeys = new[] { serverKey, signerPubKey };
        var fakeSighash = new byte[32];
        Random.Shared.NextBytes(fakeSighash);

        // Build MusigContext with the signer's actual pubkey (correct parity)
        var musigContext = new MusigContext(cosignerKeys, fakeSighash, signerPubKey);

        // This was the original crash point: GenerateNonce would throw
        // "signing pubkey doesn't match" when parity was wrong
        Assert.DoesNotThrowAsync(async () =>
        {
            await signer.GenerateNonces(descriptor, musigContext);
        });
    }

    [TestCaseSource(nameof(AllKeys))]
    public void FullMusig2Flow_WorksWithCorrectParityContext(
        string privKeyHex, string xOnlyHex)
    {
        // Full MuSig2 signing flow with correct-parity pubkey in context
        var signer = CreateSigner(privKeyHex);
        var descriptor = MakeSignerDescriptor(xOnlyHex);
        var signerPubKey = signer.GetPubKey(descriptor).Result;

        // Server-side key
        var serverPrivKeyBytes = new byte[32];
        serverPrivKeyBytes[31] = 1;
        var serverPrivKey = ECPrivKey.Create(serverPrivKeyBytes);
        var serverPub = serverPrivKey.CreatePubKey();

        var cosignerKeys = new[] { serverPub, signerPubKey };
        var fakeSighash = new byte[32];
        fakeSighash[0] = 0x42;

        // Both contexts use the correct-parity key
        var clientContext = new MusigContext(cosignerKeys, fakeSighash, signerPubKey);
        var serverContext = new MusigContext(cosignerKeys, fakeSighash, serverPub);

        // Generate nonces
        var clientNonce = signer.GenerateNonces(descriptor, clientContext).Result;
        var serverNonce = serverContext.GenerateNonce(serverPrivKey);

        var clientPubNonce = clientNonce.CreatePubNonce();
        var serverPubNonce = serverNonce.CreatePubNonce();

        // Aggregate nonces
        var allNonces = new[] { clientPubNonce, serverPubNonce };
        clientContext.ProcessNonces(allNonces);
        serverContext.ProcessNonces(allNonces);

        // Sign
        MusigPartialSignature clientSig = null!;
        Assert.DoesNotThrowAsync(async () =>
        {
            clientSig = await signer.SignMusig(descriptor, clientContext, clientNonce);
        });

        var serverSig = serverContext.Sign(serverPrivKey, serverNonce);

        // Verify partial signatures
        Assert.That(clientContext.Verify(signerPubKey, clientPubNonce, clientSig), Is.True,
            "Client partial signature should be valid");
        Assert.That(serverContext.Verify(serverPub, serverPubNonce, serverSig), Is.True,
            "Server partial signature should be valid");
    }

    [Test]
    public void AccountDescriptor_PreservesParity_ButSignerDescriptor_LosesIt()
    {
        // Verify the root cause: AccountDescriptor has the prefix, SignerDescriptor doesn't
        foreach (var key in OddParityKeys)
        {
            // AccountDescriptor has the 03 prefix preserved
            var accountDesc = MakeAccountDescriptor(key.CompressedHex);
            var accountPubKey = accountDesc.ToPubKey();
            var accountHex = Convert.ToHexString(accountPubKey.ToBytes()).ToLowerInvariant();
            Assert.That(accountHex, Does.StartWith("03"),
                "AccountDescriptor should preserve 03 prefix");

            // SignerDescriptor loses it — becomes 02
            var signerDesc = MakeSignerDescriptor(key.XOnlyHex);
            var signerPubKey = signerDesc.ToPubKey();
            var signerHex = Convert.ToHexString(signerPubKey.ToBytes()).ToLowerInvariant();
            Assert.That(signerHex, Does.StartWith("02"),
                "SignerDescriptor should lose parity (become 02)");

            // X-only keys should still match
            Assert.That(
                accountPubKey.ToXOnlyPubKey().ToBytes(),
                Is.EqualTo(signerPubKey.ToXOnlyPubKey().ToBytes()),
                "X-only keys from both descriptors should match");
        }
    }

    [Test]
    public void MusigContext_WouldFail_WithDescriptorPubKey_ForOddParityKeys()
    {
        // Proves that the OLD approach (using descriptor.ToPubKey() in MusigContext)
        // would crash for odd-parity keys
        foreach (var key in OddParityKeys)
        {
            var signer = CreateSigner(key.PrivKeyHex);
            var descriptor = MakeSignerDescriptor(key.XOnlyHex);

            // descriptor.ToPubKey() gives 02... (wrong parity)
            var wrongParityKey = descriptor.ToPubKey();

            var serverKey = ECPubKey.Create(Convert.FromHexString(
                "02381e98d39e102619de06b87a93c50e9a1cc95988bccf474cc7f26fa5df62283a"));

            var cosignerKeys = new[] { serverKey, wrongParityKey };
            var fakeSighash = new byte[32];
            fakeSighash[0] = 0x42;

            // Build context with wrong-parity key
            var musigContext = new MusigContext(cosignerKeys, fakeSighash, wrongParityKey);

            // This SHOULD throw because the private key produces 03... but context expects 02...
            Assert.Throws<ArgumentException>(() =>
            {
                musigContext.GenerateNonce(
                    ECPrivKey.Create(Convert.FromHexString(key.PrivKeyHex)));
            }, "GenerateNonce should fail when private key parity doesn't match context");
        }
    }

    private static IEnumerable<TestCaseData> AllKeys()
    {
        foreach (var k in OddParityKeys)
        {
            yield return new TestCaseData(k.PrivKeyHex, k.XOnlyHex)
                .SetName($"OddParity_{k.XOnlyHex[..8]}");
        }

        yield return new TestCaseData(EvenParityKey.PrivKeyHex, EvenParityKey.XOnlyHex)
            .SetName($"EvenParity_{EvenParityKey.XOnlyHex[..8]}");
    }
}

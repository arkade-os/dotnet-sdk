using System.Security.Cryptography;
using NArk.ArkadeIntents.Composition;

namespace NArk.Tests.ArkadeIntents.Composition;

[TestFixture]
public class SwapLinkSecretTests
{
    [Test]
    public void LoadingCopiesTheInputAndExportingDoesNotExposeTheStoredSecret()
    {
        var input = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var expectedHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

        var secret = SwapLinkSecret.FromPreimage(input);
        input[0] = 0xff;
        var exported = secret.ExportPreimage();
        exported[1] = 0xff;

        Assert.Multiple(() =>
        {
            Assert.That(secret.PaymentHash, Is.EqualTo(expectedHash));
            Assert.That(secret.ExportPreimage()[0], Is.EqualTo(1));
            Assert.That(secret.ExportPreimage()[1], Is.EqualTo(2));
        });
    }

    [TestCase(0)]
    [TestCase(31)]
    [TestCase(33)]
    public void OnlyA32BytePreimageCanLinkQuotes(int length) => Assert.That(
        () => SwapLinkSecret.FromPreimage(new byte[length]),
        Throws.ArgumentException);

    [Test]
    public void GenerateCreatesIndependentRoutes()
    {
        var first = SwapLinkSecret.Generate();
        var second = SwapLinkSecret.Generate();

        Assert.Multiple(() =>
        {
            Assert.That(first.ExportPreimage(), Has.Length.EqualTo(32));
            Assert.That(second.ExportPreimage(), Has.Length.EqualTo(32));
            Assert.That(first.PaymentHash, Is.Not.EqualTo(second.PaymentHash));
        });
    }
}
